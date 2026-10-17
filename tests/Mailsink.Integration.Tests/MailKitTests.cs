using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Mailsink.Core.Chaos;
using Mailsink.Core.Options;
using Mailsink.Testing;

namespace Mailsink.Integration.Tests;

public sealed class MailKitTests : IAsyncLifetime
{
    private MailsinkServer _mail = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _mail = await MailsinkServer.StartAsync(o => o.Auth.Mode = AuthMode.Registry, ct: Ct);
        await _mail.WithUserAsync("alice@test.local", "alice-pw", Ct);
        await _mail.WithUserAsync("bob@test.local", "bob-pw", Ct);
    }

    public async ValueTask DisposeAsync() => await _mail.DisposeAsync();

    private static MimeMessage Message(string subject, string to = "bob@test.local", string from = "app@example.com", string body = "Hello")
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private async Task SendAsync(MimeMessage message, int? port = null, (string User, string Password)? auth = null, SecureSocketOptions tls = SecureSocketOptions.None)
    {
        using var smtp = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await smtp.ConnectAsync(_mail.Host, port ?? _mail.SmtpPort, tls, Ct);
        if (auth is { } a)
        {
            await smtp.AuthenticateAsync(a.User, a.Password, Ct);
        }

        await smtp.SendAsync(message, Ct);
        await smtp.DisconnectAsync(true, Ct);
    }

    private async Task<ImapClient> ImapAsync(string user, string password, bool imaps = false)
    {
        var imap = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await imap.ConnectAsync(_mail.Host, imaps ? _mail.ImapsPort : _mail.ImapPort, imaps ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, Ct);
        await imap.AuthenticateAsync(user, password, Ct);
        return imap;
    }

    [Fact]
    public async Task Smtp_capture_then_read_over_imap()
    {
        var message = Message("Reset your password", body: "Go to https://app.test/reset?token=abc");
        message.Body = new Multipart("mixed")
        {
            new MultipartAlternative
            {
                new TextPart("plain") { Text = "Go to https://app.test/reset?token=abc" },
                new TextPart("html") { Text = "<p><a href=\"https://app.test/reset?token=abc\">Reset</a></p>" },
            },
            new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream([1, 2, 3])), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "invoice.pdf", ContentTransferEncoding = ContentEncoding.Base64 },
        };
        await SendAsync(message);

        var received = await _mail.WaitForMessageAsync(m => m.Subject == "Reset your password", ct: Ct);
        received.ShouldHaveRecipient("bob@test.local").ShouldHaveAttachment("invoice.pdf");
        Assert.Equal("https://app.test/reset?token=abc", received.HtmlBody.ShouldContainLink("https://app.test/reset"));

        using var imap = await ImapAsync("bob@test.local", "bob-pw");
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, Ct);
        Assert.Equal(1, imap.Inbox.Count);
        var summary = (await imap.Inbox.FetchAsync(0, -1, MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.UniqueId | MessageSummaryItems.Size, Ct)).Single();
        Assert.Equal("Reset your password", summary.Envelope.Subject);
        Assert.Equal("invoice.pdf", summary.Attachments.Single().FileName);

        var html = (TextPart)await imap.Inbox.GetBodyPartAsync(summary.UniqueId, summary.HtmlBody, Ct);
        Assert.Contains("Reset</a>", html.Text);
        var full = await imap.Inbox.GetMessageAsync(summary.UniqueId, Ct);
        Assert.Equal(received.Mime.MessageId, full.MessageId);
        Assert.Equal((uint)summary.Size!, (uint)_mail.Messages[0].Captured.Size);
    }

    [Fact]
    public async Task Submission_delivers_to_recipient_and_files_senders_sent_copy()
    {
        await SendAsync(Message("From Alice", from: "alice@test.local"), _mail.SubmissionPort, ("alice@test.local", "alice-pw"), SecureSocketOptions.StartTls);

        using (var bob = await ImapAsync("bob@test.local", "bob-pw", imaps: true))
        {
            await bob.Inbox.OpenAsync(FolderAccess.ReadOnly, Ct);
            Assert.Equal(1, bob.Inbox.Count);
        }

        using var alice = await ImapAsync("alice@test.local", "alice-pw");
        var sent = alice.GetFolder(SpecialFolder.Sent);
        Assert.NotNull(sent);
        await sent.OpenAsync(FolderAccess.ReadOnly, Ct);
        var item = (await sent.FetchAsync(0, -1, MessageSummaryItems.Flags | MessageSummaryItems.Envelope, Ct)).Single();
        Assert.True(item.Flags!.Value.HasFlag(MessageFlags.Seen));
        Assert.Equal("From Alice", item.Envelope.Subject);
    }

    [Fact]
    public async Task Registry_mode_rejects_bad_credentials_and_unauthenticated_submission()
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_mail.Host, _mail.SubmissionPort, SecureSocketOptions.None, Ct);
        await Assert.ThrowsAsync<AuthenticationException>(() => smtp.AuthenticateAsync("alice@test.local", "wrong", Ct));
        var ex = await Assert.ThrowsAsync<ServiceNotAuthenticatedException>(() => smtp.SendAsync(Message("x"), Ct));
        Assert.Contains("Authentication required", ex.Message);

        using var imap = new ImapClient();
        await imap.ConnectAsync(_mail.Host, _mail.ImapPort, SecureSocketOptions.None, Ct);
        await Assert.ThrowsAsync<AuthenticationException>(() => imap.AuthenticateAsync("bob@test.local", "nope", Ct));
    }

    [Fact]
    public async Task Size_limit_and_chaos_rules_produce_smtp_errors()
    {
        _mail.AddChaosRule(new ChaosRule { Stage = ChaosStage.RcptTo, Match = new ChaosMatch(Recipient: "*@bounce.test"), Response = new ChaosResponse(550, "5.1.1 User unknown") });

        var bounce = await Assert.ThrowsAsync<SmtpCommandException>(() => SendAsync(Message("bounce", to: "x@bounce.test")));
        Assert.Equal(SmtpStatusCode.MailboxUnavailable, bounce.StatusCode);

        await using var small = await MailsinkServer.StartAsync(o => o.Smtp.MaxMessageSize = 1024, ct: Ct);
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(small.Host, small.SmtpPort, SecureSocketOptions.None, Ct);
        var tooBig = await Assert.ThrowsAsync<SmtpCommandException>(() => smtp.SendAsync(Message("big", body: new string('x', 5000)), Ct));
        Assert.Equal(SmtpStatusCode.ExceededStorageAllocation, tooBig.StatusCode);
    }

    [Fact]
    public async Task Idle_receives_new_mail_and_flag_changes_from_other_sessions()
    {
        await SendAsync(Message("first"));
        await _mail.WaitForMessagesAsync(1, ct: Ct);

        using var watcher = await ImapAsync("bob@test.local", "bob-pw");
        using var actor = await ImapAsync("bob@test.local", "bob-pw");
        await watcher.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        await actor.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);

        var arrived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flagged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Inbox.CountChanged += (_, _) => arrived.TrySetResult(watcher.Inbox.Count);
        watcher.Inbox.MessageFlagsChanged += (_, e) =>
        {
            if (e.Flags.HasFlag(MessageFlags.Flagged))
            {
                flagged.TrySetResult();
            }
        };

        using var idleDone = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var idle = watcher.IdleAsync(idleDone.Token, Ct);

        await SendAsync(Message("second"));
        Assert.Equal(2, await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        await actor.Inbox.AddFlagsAsync(0, MessageFlags.Flagged, true, Ct);
        await flagged.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await idleDone.CancelAsync();
        await idle;
    }

    [Fact]
    public async Task Expunge_in_one_session_is_reported_to_another_at_a_safe_point()
    {
        await SendAsync(Message("one"));
        await SendAsync(Message("two"));
        await SendAsync(Message("three"));
        await _mail.WaitForMessagesAsync(3, ct: Ct);

        using var a = await ImapAsync("bob@test.local", "bob-pw");
        using var b = await ImapAsync("bob@test.local", "bob-pw");
        await a.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        await b.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        var expunged = new List<int>();
        b.Inbox.MessageExpunged += (_, e) => expunged.Add(e.Index);

        await a.Inbox.AddFlagsAsync(1, MessageFlags.Deleted, true, Ct);
        await a.Inbox.ExpungeAsync(Ct);
        Assert.Equal(2, a.Inbox.Count);

        await b.NoOpAsync(Ct);
        Assert.Equal([1], expunged);
        Assert.Equal(2, b.Inbox.Count);
        var subjects = (await b.Inbox.FetchAsync(0, -1, MessageSummaryItems.Envelope, Ct)).Select(s => s.Envelope.Subject);
        Assert.Equal(["one", "three"], subjects);
    }

    [Fact]
    public async Task Folders_append_copy_move_search()
    {
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(Message(i == 1 ? "Invoice 42" : $"Note {i}"));
        }

        await _mail.WaitForMessagesAsync(3, ct: Ct);
        using var imap = await ImapAsync("bob@test.local", "bob-pw");
        var root = imap.GetFolder(imap.PersonalNamespaces[0]);
        var archive = await root.CreateAsync("Archive", true, Ct);
        var year = await archive.CreateAsync("2026", true, Ct);
        await year.RenameAsync(archive, "Old", Ct);
        var folders = (await root.GetSubfoldersAsync(false, Ct)).Select(f => f.FullName).ToList();
        Assert.Contains("Archive", folders);

        var drafts = imap.GetFolder(SpecialFolder.Drafts);
        var appended = await drafts.AppendAsync(new AppendRequest(Message("draft")) { Flags = MessageFlags.Draft }, Ct);
        Assert.NotNull(appended);

        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        var hits = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains("invoice").And(SearchQuery.NotSeen), Ct);
        Assert.Single(hits);
        var text = await imap.Inbox.SearchAsync(SearchQuery.BodyContains("Hello"), Ct);
        Assert.Equal(3, text.Count);

        var copied = await imap.Inbox.CopyToAsync(hits[0], archive, Ct);
        Assert.NotNull(copied);
        var moved = await imap.Inbox.MoveToAsync(hits[0], imap.GetFolder(SpecialFolder.Trash), Ct);
        Assert.NotNull(moved);
        Assert.Equal(2, imap.Inbox.Count);

        await archive.OpenAsync(FolderAccess.ReadOnly, Ct);
        Assert.Equal(1, archive.Count);
        await year.DeleteAsync(Ct);
    }

    [Fact]
    public async Task Pop3_lists_retrieves_and_deletes()
    {
        await using var pop = await MailsinkServer.StartAsync(ct: Ct);
        await pop.SendAsync(Message("pop me", to: "carol@test.local"), Ct);
        await pop.SendAsync(Message("pop me too", to: "carol@test.local"), Ct);

        using (var client = new Pop3Client())
        {
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync(pop.Host, pop.Pop3Port, SecureSocketOptions.StartTlsWhenAvailable, Ct);
            await client.AuthenticateAsync("carol@test.local", "x", Ct);
            Assert.Equal(2, client.Count);
            var message = await client.GetMessageAsync(0, Ct);
            Assert.Equal("pop me", message.Subject);
            await client.DeleteMessageAsync(0, Ct);
            await client.DisconnectAsync(true, Ct);
        }

        using var again = new Pop3Client();
        await again.ConnectAsync(pop.Host, pop.Pop3Port, SecureSocketOptions.None, Ct);
        await again.AuthenticateAsync("carol@test.local", "x", Ct);
        Assert.Equal(1, again.Count);
    }
}

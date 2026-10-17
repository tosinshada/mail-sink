using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using Mailsink.Testing;

namespace Mailsink.Imap.Tests;

/// <summary>
/// Golden C:/S: transcripts replayed against an in-process server. Server lines are regexes, so UIDVALIDITY and
/// dates can vary; everything else must match exactly and in order.
/// </summary>
public sealed class TranscriptTests : IAsyncLifetime
{
    private MailsinkServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await MailsinkServer.StartAsync(ct: TestContext.Current.CancellationToken);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("App", "app@example.com"));
        message.To.Add(new MailboxAddress("", "bob@test.local"));
        message.Subject = "Reset your password";
        message.Date = new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
        message.MessageId = "reset-1@example.com";
        message.Body = new TextPart("plain") { Text = "Click the link." };
        await _server.SendAsync(message, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public Task Login_list_select_fetch_move() => RunAsync("""
        S: \* OK \[CAPABILITY IMAP4rev1 .*AUTH=PLAIN.*\] Mailsink ready
        C: a1 LOGIN bob@test.local pw
        S: a1 OK \[CAPABILITY IMAP4rev1 .* UIDPLUS MOVE SPECIAL-USE .*\] Logged in
        C: a2 LIST "" "*"
        S: \* LIST \(\\HasNoChildren\) "/" INBOX
        S: \* LIST \(\\HasNoChildren \\Drafts\) "/" Drafts
        S: \* LIST \(\\HasNoChildren \\Sent\) "/" Sent
        S: \* LIST \(\\HasNoChildren \\Trash\) "/" Trash
        S: a2 OK LIST completed
        C: a3 SELECT INBOX
        S: \* FLAGS \(\\Answered \\Flagged \\Deleted \\Seen \\Draft\)
        S: \* OK \[PERMANENTFLAGS \(\\Answered \\Flagged \\Deleted \\Seen \\Draft \\\*\)\] Flags permitted
        S: \* 1 EXISTS
        S: \* 1 RECENT
        S: \* OK \[UNSEEN 1\] First unseen
        S: \* OK \[UIDVALIDITY \d+\] UIDs valid
        S: \* OK \[UIDNEXT 2\] Predicted next UID
        S: \* OK \[HIGHESTMODSEQ \d+\] Highest
        S: a3 OK \[READ-WRITE\] SELECT completed
        C: a4 UID FETCH 1:* (FLAGS ENVELOPE BODY.PEEK[HEADER.FIELDS (SUBJECT)])
        S: \* 1 FETCH \(UID 1 FLAGS \(\\Recent\) ENVELOPE \("Sat, 17 Oct 2026 10:00:00 \+0000" "Reset your password" \(\("App" NIL "app" "example.com"\)\) .* NIL "<reset-1@example.com>"\) BODY\[HEADER.FIELDS \(SUBJECT\)\] \{32\}
        S: Subject: Reset your password
        S:
        S: \)
        S: a4 OK FETCH completed
        C: a5 STORE 1 +FLAGS (\Flagged)
        S: \* 1 FETCH \(FLAGS \(\\Flagged \\Recent\)\)
        S: a5 OK STORE completed
        C: a6 UID MOVE 1 Trash
        S: \* OK \[COPYUID \d+ 1 1\] Moved
        S: \* 1 EXPUNGE
        S: a6 OK MOVE completed
        C: a7 LOGOUT
        S: \* BYE Mailsink logging out
        S: a7 OK LOGOUT completed
        """);

    [Fact]
    public Task Search_status_append_and_errors() => RunAsync("""
        S: \* OK .*
        C: b1 LOGIN bob pw
        S: b1 OK .*
        C: b2 STATUS INBOX (MESSAGES UNSEEN UIDNEXT)
        S: \* STATUS INBOX \(MESSAGES 1 UNSEEN 1 UIDNEXT 2\)
        S: b2 OK STATUS completed
        C: b3 APPEND Drafts (\Draft) {29+}
        C: Subject: draft
        C:
        C: hello there
        S: b3 OK \[APPENDUID \d+ 1\] APPEND completed
        C: b4 APPEND Nope {2+}
        C: hi
        S: b4 NO \[TRYCREATE\] Mailbox does not exist
        C: b5 FETCH 1 FLAGS
        S: b5 BAD FETCH not allowed in this state
        C: b6 EXAMINE INBOX
        S: \* FLAGS .*
        S: \* OK \[PERMANENTFLAGS \(\)\] Flags permitted
        S: \* 1 EXISTS
        S: \* 0 RECENT
        S: \* OK \[UNSEEN 1\] First unseen
        S: \* OK \[UIDVALIDITY \d+\] UIDs valid
        S: \* OK \[UIDNEXT 2\] Predicted next UID
        S: \* OK \[HIGHESTMODSEQ \d+\] Highest
        S: b6 OK \[READ-ONLY\] EXAMINE completed
        C: b7 SEARCH SUBJECT reset UNSEEN
        S: \* SEARCH 1
        S: b7 OK SEARCH completed
        C: b8 UID SEARCH RETURN (MIN COUNT) ALL
        S: \* ESEARCH \(TAG "b8"\) UID MIN 1 COUNT 1
        S: b8 OK SEARCH completed
        C: b9 SEARCH CHARSET KOI8-R ALL
        S: b9 NO \[BADCHARSET \(US-ASCII UTF-8\)\] Unsupported charset
        C: b10 STORE 1 +FLAGS (\Seen)
        S: b10 NO \[READ-ONLY\] Mailbox is read-only
        C: b11 FETCH 1 (BODY[TEXT])
        S: \* 1 FETCH \(BODY\[TEXT\] \{17\}
        S: Click the link.
        S: \)
        S: b11 OK FETCH completed
        C: b12 BOGUS
        S: b12 BAD Unknown command
        """);

    private async Task RunAsync(string script)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _server.ImapPort, ct);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lineNumber = 0;
        foreach (var raw in script.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("C: ", StringComparison.Ordinal) || line == "C:")
            {
                var bytes = Encoding.UTF8.GetBytes((line.Length > 3 ? line[3..] : "") + "\r\n");
                await stream.WriteAsync(bytes, ct);
            }
            else if (line.StartsWith("S:", StringComparison.Ordinal))
            {
                var pattern = line.Length > 3 ? line[3..] : "";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var actual = await reader.ReadLineAsync(timeout.Token) ?? "(eof)";
                Assert.True(Regex.IsMatch(actual, "^" + pattern + "$"), $"Line {lineNumber}: expected /{pattern}/ but got: {actual}");
            }
        }
    }
}

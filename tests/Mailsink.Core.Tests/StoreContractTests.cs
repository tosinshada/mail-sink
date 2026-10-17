using System.Text;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Search;
using Mailsink.Core.Storage;
using Mailsink.Storage.InMemory;
using Mailsink.Storage.Sqlite;

namespace Mailsink.Core.Tests;

/// <summary>The same behaviour is required of every <see cref="IMailStore"/>.</summary>
public abstract class StoreContractTests : IAsyncLifetime
{
    protected MailEventBus Bus { get; } = new();

    protected IMailStore Store { get; private set; } = null!;

    protected abstract IMailStore CreateStore();

    public virtual async ValueTask InitializeAsync()
    {
        Store = CreateStore();
        await Store.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Eml(string subject, string body = "body", string to = "bob@test.local") =>
        Encoding.UTF8.GetBytes($"From: alice@test.local\nTo: {to}\nSubject: {subject}\nDate: Fri, 17 Oct 2026 10:00:00 +0000\n\n{body}\n");

    private async Task<Mailbox> InboxAsync(string owner = "bob@test.local")
    {
        await Store.CreateAccountAsync(owner, null, Ct);
        return await Store.GetMailboxAsync(owner, "INBOX", Ct) ?? await Store.CreateMailboxAsync(owner, "INBOX", SpecialUse.None, Ct);
    }

    [Fact]
    public async Task Capture_normalizes_and_query_finds_by_terms()
    {
        var m = await Store.CaptureAsync(Eml("Weekly report", "numbers inside"), new Envelope("alice@test.local", ["bob@test.local"], MessageSource.Smtp), true, Ct);
        await Store.CaptureAsync(Eml("Other"), new Envelope("x@test.local", ["carol@test.local"], MessageSource.Api), true, Ct);

        Assert.Contains("\r\n", Encoding.UTF8.GetString(await Store.ReadRawAsync(m.Id, Ct)));
        Assert.Equal(1, (await Store.QueryCaptureAsync(new CaptureQuery("subject:weekly"), Ct)).Total);
        Assert.Equal(1, (await Store.QueryCaptureAsync(new CaptureQuery("numbers"), Ct)).Total);
        Assert.Equal(1, (await Store.QueryCaptureAsync(new CaptureQuery("to:carol"), Ct)).Total);
        Assert.Equal(1, (await Store.QueryCaptureAsync(new CaptureQuery("-subject:weekly"), Ct)).Total);
        Assert.Equal(1, (await Store.QueryCaptureAsync(new CaptureQuery(Source: MessageSource.Api), Ct)).Total);
        Assert.Equal(2, (await Store.QueryCaptureAsync(new CaptureQuery(), Ct)).Total);
    }

    [Fact]
    public async Task Append_assigns_increasing_uids_and_modseq()
    {
        var inbox = await InboxAsync();
        var m = await Store.CaptureAsync(Eml("a"), new Envelope(null, [], MessageSource.Smtp), true, Ct);

        var e1 = await Store.AppendAsync(inbox.Id, m.Id, SystemFlags.None, [], DateTimeOffset.UtcNow, Ct);
        var e2 = await Store.AppendAsync(inbox.Id, m.Id, SystemFlags.Seen, ["$Label1"], DateTimeOffset.UtcNow, Ct);

        Assert.Equal(1u, e1.Uid);
        Assert.Equal(2u, e2.Uid);
        Assert.True(e2.ModSeq > e1.ModSeq);
        var box = await Store.GetMailboxAsync(inbox.Id, Ct);
        Assert.Equal(3u, box!.UidNext);
        Assert.Equal(["$Label1"], (await Store.GetEntriesAsync(inbox.Id, Ct))[1].Keywords);
    }

    [Fact]
    public async Task Store_flags_publishes_only_real_changes()
    {
        var inbox = await InboxAsync();
        var m = await Store.CaptureAsync(Eml("a"), new Envelope(null, [], MessageSource.Smtp), true, Ct);
        var entry = await Store.AppendAsync(inbox.Id, m.Id, SystemFlags.Seen, [], DateTimeOffset.UtcNow, Ct);
        using var subscription = Bus.Subscribe();

        var unchanged = await Store.StoreFlagsAsync(inbox.Id, [entry.Uid], FlagOperation.Add, SystemFlags.Seen, [], Ct);
        var changed = await Store.StoreFlagsAsync(inbox.Id, [entry.Uid], FlagOperation.Add, SystemFlags.Flagged, ["work"], Ct);

        Assert.Single(unchanged);
        Assert.Equal(SystemFlags.Seen | SystemFlags.Flagged, changed[0].Flags);
        Assert.True(changed[0].ModSeq > entry.ModSeq);
        var events = new List<MailEvent>();
        while (subscription.Reader.TryRead(out var e))
        {
            events.Add(e);
        }

        Assert.Single(events.OfType<EntryFlagsChangedEvent>());
    }

    [Fact]
    public async Task Move_and_expunge()
    {
        var inbox = await InboxAsync();
        var trash = await Store.CreateMailboxAsync("bob@test.local", "Trash", SpecialUse.Trash, Ct);
        var m = await Store.CaptureAsync(Eml("a"), new Envelope(null, [], MessageSource.Smtp), true, Ct);
        var e1 = await Store.AppendAsync(inbox.Id, m.Id, SystemFlags.None, [], DateTimeOffset.UtcNow, Ct);
        var e2 = await Store.AppendAsync(inbox.Id, m.Id, SystemFlags.None, [], DateTimeOffset.UtcNow, Ct);

        var moved = await Store.CopyAsync(inbox.Id, [e1.Uid], trash.Id, move: true, Ct);
        Assert.Equal([e1.Uid], moved.SourceUids);
        Assert.Equal([1u], moved.DestinationUids);
        Assert.Equal(trash.UidValidity, moved.UidValidity);
        Assert.Equal([e2.Uid], (await Store.GetEntriesAsync(inbox.Id, Ct)).Select(e => e.Uid));

        await Store.StoreFlagsAsync(trash.Id, [1], FlagOperation.Add, SystemFlags.Deleted, [], Ct);
        Assert.Equal([1u], await Store.ExpungeAsync(trash.Id, null, Ct));
        Assert.Empty(await Store.GetEntriesAsync(trash.Id, Ct));
    }

    [Fact]
    public async Task Deleting_from_capture_view_keeps_mailbox_copies()
    {
        var inbox = await InboxAsync();
        var m = await Store.CaptureAsync(Eml("keep me"), new Envelope(null, [], MessageSource.Smtp), true, Ct);
        await Store.AppendAsync(inbox.Id, m.Id, SystemFlags.None, [], DateTimeOffset.UtcNow, Ct);

        await Store.DeleteCapturedAsync([m.Id], Ct);

        Assert.Equal(0, (await Store.QueryCaptureAsync(new CaptureQuery(), Ct)).Total);
        Assert.NotEmpty(await Store.ReadRawAsync(m.Id, Ct));

        // Once the last mailbox reference goes, the message and its blob are collected.
        await Store.StoreFlagsAsync(inbox.Id, [1], FlagOperation.Add, SystemFlags.Deleted, [], Ct);
        await Store.ExpungeAsync(inbox.Id, null, Ct);
        Assert.Null(await Store.GetMessageAsync(m.Id, Ct));
    }

    [Fact]
    public async Task Rename_moves_children_and_inbox_rename_empties_inbox()
    {
        var inbox = await InboxAsync();
        var parent = await Store.CreateMailboxAsync("bob@test.local", "Projects", SpecialUse.None, Ct);
        await Store.CreateMailboxAsync("bob@test.local", "Projects/Alpha", SpecialUse.None, Ct);
        await Store.RenameMailboxAsync(parent.Id, "Work", Ct);
        var names = (await Store.ListMailboxesAsync("bob@test.local", Ct)).Select(b => b.Name).ToList();
        Assert.Contains("Work/Alpha", names);
        Assert.DoesNotContain("Projects", names);

        var m = await Store.CaptureAsync(Eml("a"), new Envelope(null, [], MessageSource.Smtp), true, Ct);
        await Store.AppendAsync(inbox.Id, m.Id, SystemFlags.None, [], DateTimeOffset.UtcNow, Ct);
        await Store.RenameMailboxAsync(inbox.Id, "Old", Ct);
        Assert.Empty(await Store.GetEntriesAsync(inbox.Id, Ct));
        var old = await Store.GetMailboxAsync("bob@test.local", "Old", Ct);
        Assert.Single(await Store.GetEntriesAsync(old!.Id, Ct));

        await Assert.ThrowsAsync<MailStoreException>(() => Store.DeleteMailboxAsync(inbox.Id, Ct));
    }

    [Fact]
    public async Task Tags_read_state_and_stats()
    {
        var m = await Store.CaptureAsync(Eml("a"), new Envelope(null, [], MessageSource.Smtp), true, Ct);
        await Store.SetTagsAsync(m.Id, ["Urgent", "urgent", " billing "], Ct);
        await Store.SetReadAsync([m.Id], true, Ct);

        var loaded = await Store.GetMessageAsync(m.Id, Ct);
        Assert.Equal(2, loaded!.Tags.Count);
        Assert.True(loaded.IsRead);
        Assert.Equal(1, (await Store.QueryCaptureAsync(new CaptureQuery("tag:billing"), Ct)).Total);
        Assert.Equal(new CaptureStats(1, 0, loaded.Size), await Store.GetCaptureStatsAsync(Ct));
    }

    [Fact]
    public async Task Retention_keeps_newest_messages()
    {
        for (var i = 0; i < 5; i++)
        {
            await Store.CaptureAsync(Eml($"m{i}"), new Envelope(null, [], MessageSource.Smtp), true, Ct);
            await Task.Delay(2, Ct);
        }

        Assert.Equal(3, await Store.ApplyRetentionAsync(new RetentionPolicy(2, null, null, false), Ct));
        var left = (await Store.QueryCaptureAsync(new CaptureQuery(), Ct)).Items.Select(m => m.Summary.Subject);
        Assert.Equal(["m4", "m3"], left);
    }

    [Fact]
    public async Task Purge_removes_everything()
    {
        await InboxAsync();
        await Store.CaptureAsync(Eml("a"), new Envelope(null, [], MessageSource.Smtp), true, Ct);

        await Store.PurgeAsync(Ct);

        Assert.Empty(await Store.ListAccountsAsync(Ct));
        Assert.Equal(0, (await Store.GetCaptureStatsAsync(Ct)).Total);
    }
}

public sealed class InMemoryStoreTests : StoreContractTests
{
    protected override IMailStore CreateStore() => new InMemoryMailStore(new InMemoryBlobStore(), Bus, new ParsedMessageCache());
}

public sealed class SqliteStoreTests : StoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailsink-tests", Guid.NewGuid().ToString("N"));

    protected override IMailStore CreateStore()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMailStore(new SqliteDatabase(Path.Combine(_dir, "test.db")), new FileBlobStore(Path.Combine(_dir, "blobs")), Bus, new ParsedMessageCache());
    }

    public override ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort on Windows if a handle lingers.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class SqliteDatabaseBlobStoreTests : StoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailsink-tests", Guid.NewGuid().ToString("N"));

    protected override IMailStore CreateStore()
    {
        Directory.CreateDirectory(_dir);
        var db = new SqliteDatabase(Path.Combine(_dir, "test.db"));
        return new SqliteMailStore(db, new SqliteBlobStore(db), Bus, new ParsedMessageCache());
    }
}

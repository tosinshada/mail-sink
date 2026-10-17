using MimeKit;
using Mailsink.Core.Domain;
using Mailsink.Core.Search;

namespace Mailsink.Core.Storage;

/// <summary>
/// The single source of truth for accounts, mailboxes, mailbox entries and captured messages.
/// Every mutation is atomic, bumps modseq where relevant, and publishes events on the bus.
/// </summary>
public interface IMailStore
{
    Task InitializeAsync(CancellationToken ct);

    // Accounts
    Task<Account?> FindAccountAsync(string address, CancellationToken ct);
    Task<Account> CreateAccountAsync(string address, byte[]? passwordHash, CancellationToken ct);
    Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct);
    Task<bool> DeleteAccountAsync(string address, CancellationToken ct);
    Task SetPasswordAsync(string address, byte[]? passwordHash, CancellationToken ct);
    Task RecordLoginAsync(string address, string protocol, CancellationToken ct);

    // Capture + delivery
    Task<CapturedMessage> CaptureAsync(ReadOnlyMemory<byte> rawMime, Envelope envelope, bool inCaptureView, CancellationToken ct);
    Task<MailboxEntry> AppendAsync(MailboxId mailbox, MessageId message, SystemFlags flags, IReadOnlyCollection<string> keywords, DateTimeOffset internalDate, CancellationToken ct);

    // Mailboxes
    Task<IReadOnlyList<Mailbox>> ListMailboxesAsync(string owner, CancellationToken ct);
    Task<Mailbox?> GetMailboxAsync(string owner, string name, CancellationToken ct);
    Task<Mailbox?> GetMailboxAsync(MailboxId id, CancellationToken ct);
    Task<Mailbox> CreateMailboxAsync(string owner, string name, SpecialUse specialUse, CancellationToken ct);
    Task RenameMailboxAsync(MailboxId id, string newName, CancellationToken ct);
    Task DeleteMailboxAsync(MailboxId id, CancellationToken ct);
    Task SetSubscribedAsync(MailboxId id, bool subscribed, CancellationToken ct);
    Task<MailboxStats> GetMailboxStatsAsync(MailboxId id, CancellationToken ct);

    // IMAP-facing entry operations
    Task<IReadOnlyList<MailboxEntry>> GetEntriesAsync(MailboxId mailbox, CancellationToken ct);
    Task<IReadOnlyList<MailboxEntry>> StoreFlagsAsync(MailboxId mailbox, IReadOnlyCollection<uint> uids, FlagOperation op, SystemFlags flags, IReadOnlyCollection<string> keywords, CancellationToken ct);
    Task<CopyResult> CopyAsync(MailboxId from, IReadOnlyCollection<uint> uids, MailboxId to, bool move, CancellationToken ct);
    Task<IReadOnlyList<uint>> ExpungeAsync(MailboxId mailbox, IReadOnlyCollection<uint>? onlyUids, CancellationToken ct);

    // Content
    Task<CapturedMessage?> GetMessageAsync(MessageId id, CancellationToken ct);
    Task<IReadOnlyDictionary<MessageId, CapturedMessage>> GetMessagesAsync(IReadOnlyCollection<MessageId> ids, CancellationToken ct);
    Task<byte[]> ReadRawAsync(MessageId id, CancellationToken ct);
    Task<MimeMessage> LoadParsedAsync(MessageId id, CancellationToken ct);

    // Capture view
    Task<PagedResult<CapturedMessage>> QueryCaptureAsync(CaptureQuery query, CancellationToken ct);
    Task<CaptureStats> GetCaptureStatsAsync(CancellationToken ct);
    Task SetReadAsync(IReadOnlyCollection<MessageId>? ids, bool read, CancellationToken ct);
    Task SetTagsAsync(MessageId id, IReadOnlyCollection<string> tags, CancellationToken ct);
    Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken ct);
    Task DeleteCapturedAsync(IReadOnlyCollection<MessageId>? ids, CancellationToken ct);
    Task<int> ApplyRetentionAsync(RetentionPolicy policy, CancellationToken ct);

    /// <summary>Deletes everything: messages, mailboxes and accounts (accounts seeded from config are re-created by the caller).</summary>
    Task PurgeAsync(CancellationToken ct);
}

/// <summary>Content-addressed storage for raw <c>.eml</c> bytes.</summary>
public interface IBlobStore
{
    Task PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task DeleteAllAsync(CancellationToken ct);
}

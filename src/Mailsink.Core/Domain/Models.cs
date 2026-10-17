namespace Mailsink.Core.Domain;

public enum MessageSource { Smtp = 0, Submission = 1, ImapAppend = 2, Api = 3, Ui = 4 }

public sealed record EmailAddress(string? Name, string Address)
{
    public override string ToString() => string.IsNullOrEmpty(Name) ? Address : $"{Name} <{Address}>";
}

/// <summary>The SMTP (or synthetic) envelope a message arrived with.</summary>
public sealed record Envelope(
    string? MailFrom,
    IReadOnlyList<string> RcptTo,
    MessageSource Source,
    string? Helo = null,
    string? ClientIp = null,
    bool Secure = false,
    string? AuthUser = null,
    string? SessionId = null);

public sealed record ParsedSummary(
    string? MessageIdHeader,
    string? Subject,
    EmailAddress? From,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    IReadOnlyList<EmailAddress> Bcc,
    IReadOnlyList<EmailAddress> ReplyTo,
    DateTimeOffset? Date,
    string Snippet,
    bool HasAttachments,
    int AttachmentCount);

/// <summary>IMAP ENVELOPE / BODYSTRUCTURE / BODY, rendered once at capture time.</summary>
public sealed record ImapRendering(string Envelope, string BodyStructure, string Body);

public sealed record CapturedMessage(
    MessageId Id,
    string BlobKey,
    long Size,
    DateTimeOffset ReceivedAt,
    Envelope Envelope,
    ParsedSummary Summary,
    ImapRendering Imap,
    IReadOnlyList<string> Tags,
    bool IsRead,
    bool InCaptureView)
{
    public MessageSource Source => Envelope.Source;
}

public sealed record Mailbox(
    MailboxId Id,
    string Owner,
    string Name,
    uint UidValidity,
    uint UidNext,
    ulong HighestModSeq,
    SpecialUse SpecialUse,
    bool Subscribed);

public sealed record MailboxEntry(
    MailboxId Mailbox,
    uint Uid,
    MessageId Message,
    SystemFlags Flags,
    IReadOnlyList<string> Keywords,
    DateTimeOffset InternalDate,
    ulong ModSeq,
    long Size);

public sealed record MailboxStats(int Messages, int Unseen, long Size);

public sealed record Account(
    long Id,
    string Address,
    byte[]? PasswordHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    string? LastLoginProtocol);

public sealed record CopyResult(uint UidValidity, IReadOnlyList<uint> SourceUids, IReadOnlyList<uint> DestinationUids);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Start, int Limit);

public sealed record CaptureStats(int Total, int Unread, long TotalSize);

public sealed record RetentionPolicy(int MaxMessages, TimeSpan? MaxAge, long? MaxTotalSize, bool AppliesToMailboxes);

public sealed class MailStoreException(string message) : Exception(message);

using System.Text.Json;
using System.Text.Json.Serialization;
using Mailsink.Core.Chaos;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Domain;
using Mailsink.Core.Templates;

namespace Mailsink.Api;

public sealed record AddressDto(string? Name, string Email)
{
    public static AddressDto From(EmailAddress a) => new(a.Name, a.Address);
}

public sealed record MessageSummaryDto(
    string Id,
    string? MessageId,
    string? Subject,
    AddressDto? From,
    IReadOnlyList<AddressDto> To,
    IReadOnlyList<AddressDto> Cc,
    IReadOnlyList<AddressDto> Bcc,
    DateTimeOffset? Date,
    DateTimeOffset ReceivedAt,
    long Size,
    string Snippet,
    bool IsRead,
    IReadOnlyList<string> Tags,
    bool HasAttachments,
    int AttachmentCount,
    string Source,
    IReadOnlyList<string> Recipients);

public sealed record MessageListResponse(int Total, int Unread, int Start, int Limit, IReadOnlyList<MessageSummaryDto> Messages, IReadOnlyList<string> Tags);

public sealed record EnvelopeDto(string? MailFrom, IReadOnlyList<string> RcptTo, string? Helo, string? ClientIp, bool Secure, string? AuthUser, string? SessionId, string Source);

public sealed record AttachmentDto(string PartId, string? FileName, string ContentType, long Size, string? ContentId, bool Inline);

public sealed record MessageDetailDto(
    MessageSummaryDto Summary,
    IReadOnlyList<AddressDto> ReplyTo,
    EnvelopeDto Envelope,
    string? Text,
    string? Html,
    IReadOnlyList<AttachmentDto> Attachments);

public sealed record HeaderDto(string Name, string Value);

public sealed record IdsRequest(IReadOnlyList<string>? Ids);

public sealed record TagsRequest(IReadOnlyList<string> Tags);

public sealed record AttachmentInput(string Filename, string? ContentType, string ContentBase64);

public sealed record InlineInput(string ContentId, string? ContentType, string ContentBase64, string? Filename);

public sealed record SendRequest(
    AddressDto? From,
    IReadOnlyList<AddressDto>? To,
    IReadOnlyList<AddressDto>? Cc,
    IReadOnlyList<AddressDto>? Bcc,
    IReadOnlyList<AddressDto>? ReplyTo,
    string? Subject,
    string? Text,
    string? Html,
    Dictionary<string, string>? Headers,
    IReadOnlyList<AttachmentInput>? Attachments,
    IReadOnlyList<InlineInput>? Inline,
    JsonElement? TemplateData,
    string? Template,
    int Repeat = 1,
    IReadOnlyList<string>? Tags = null);

public sealed record BulkSendRequest(
    int Count = 10,
    AddressDto? From = null,
    IReadOnlyList<string>? To = null,
    bool Random = true,
    string? Subject = null,
    string? Text = null,
    string? Html = null,
    string? Template = null,
    JsonElement? TemplateData = null,
    bool WithAttachments = false);

public sealed record SendResponse(IReadOnlyList<string> Ids, string? MessageId);

public sealed record AccountDto(string Address, bool HasPassword, int Mailboxes, int Messages, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, string? LastLoginProtocol);

public sealed record CreateAccountRequest(string Address, string? Password);

public sealed record PasswordRequest(string? Password);

public sealed record MailboxDto(long Id, string Name, string? SpecialUse, int Messages, int Unseen, long Size, uint UidValidity, uint UidNext, bool Subscribed);

public sealed record CreateMailboxRequest(string Name);

public sealed record RenameMailboxRequest(string NewName);

public sealed record MailboxMessageDto(uint Uid, IReadOnlyList<string> Flags, DateTimeOffset InternalDate, long Size, MessageSummaryDto Message);

public sealed record MailboxMessagesResponse(MailboxDto Mailbox, int Total, int Start, int Limit, IReadOnlyList<MailboxMessageDto> Messages);

public sealed record MoveRequest(IReadOnlyList<uint> Uids, string Target, bool Copy = false);

public sealed record FlagsRequest(IReadOnlyList<uint> Uids, IReadOnlyList<string>? Add, IReadOnlyList<string>? Remove);

public sealed record EndpointDto(string Name, string Protocol, int Port, bool ImplicitTls, string Address);

public sealed record RetentionDto(int MaxMessages, double? MaxAgeHours, long? MaxTotalSize, bool AppliesToMailboxes);

public sealed record InfoResponse(
    string Version,
    string Hostname,
    string DefaultDomain,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    IReadOnlyList<EndpointDto> Endpoints,
    int Messages,
    int Unread,
    long StoreSize,
    int Accounts,
    string Storage,
    string DataDir,
    string AuthMode,
    bool RequireTls,
    bool TlsAvailable,
    RetentionDto Retention,
    long MaxMessageSize,
    bool UiAuth);

public sealed record HealthResponse(string Status, double UptimeSeconds);

public sealed record TranscriptLineDto(DateTimeOffset At, string Direction, string Text);

public sealed record TranscriptDto(TranscriptSummary Session, IReadOnlyList<TranscriptLineDto> Lines);

public sealed record MetricsResponse(MetricsSnapshot Metrics, int Messages, int Unread, long StoreSize, int Accounts);

public sealed record StatsDto(int Total, int Unread);

public sealed record MessageUpdatedDto(string Id, bool IsRead, IReadOnlyList<string> Tags);

public sealed record MailboxChangedDto(string Owner, long? MailboxId);

public sealed record WaitResponse(int Count, IReadOnlyList<MessageSummaryDto> Messages);

public sealed record ErrorResponse(string Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(MessageSummaryDto))]
[JsonSerializable(typeof(MessageListResponse))]
[JsonSerializable(typeof(MessageDetailDto))]
[JsonSerializable(typeof(IReadOnlyList<HeaderDto>))]
[JsonSerializable(typeof(List<HeaderDto>))]
[JsonSerializable(typeof(IdsRequest))]
[JsonSerializable(typeof(TagsRequest))]
[JsonSerializable(typeof(SendRequest))]
[JsonSerializable(typeof(BulkSendRequest))]
[JsonSerializable(typeof(SendResponse))]
[JsonSerializable(typeof(List<AccountDto>))]
[JsonSerializable(typeof(CreateAccountRequest))]
[JsonSerializable(typeof(PasswordRequest))]
[JsonSerializable(typeof(List<MailboxDto>))]
[JsonSerializable(typeof(MailboxDto))]
[JsonSerializable(typeof(CreateMailboxRequest))]
[JsonSerializable(typeof(RenameMailboxRequest))]
[JsonSerializable(typeof(MailboxMessagesResponse))]
[JsonSerializable(typeof(MoveRequest))]
[JsonSerializable(typeof(FlagsRequest))]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(List<TranscriptSummary>))]
[JsonSerializable(typeof(TranscriptDto))]
[JsonSerializable(typeof(MetricsResponse))]
[JsonSerializable(typeof(StatsDto))]
[JsonSerializable(typeof(MessageUpdatedDto))]
[JsonSerializable(typeof(MailboxChangedDto))]
[JsonSerializable(typeof(WaitResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ChaosSettings))]
[JsonSerializable(typeof(ChaosRule))]
[JsonSerializable(typeof(List<SendTemplate>))]
[JsonSerializable(typeof(SendTemplate))]
[JsonSerializable(typeof(TemplateInput))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
public sealed partial class ApiJsonContext : JsonSerializerContext;

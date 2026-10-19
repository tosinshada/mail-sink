using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Mailsink.Core.Events;
using Mailsink.Core.Options;

namespace Mailsink.Core.Sms;

/// <summary>A captured text message. One record per recipient; <see cref="To"/> is the normalized number.</summary>
public sealed record SmsMessage(Guid Id, string To, string FromName, string FromNumber, string Body, DateTimeOffset ReceivedAt, bool IsRead);

/// <summary>Who sent a message. Missing parts fall back to <see cref="SmsOptions"/> defaults.</summary>
public sealed record SmsSender(string Name, string Number);

/// <summary>One phone (recipient number) with metrics for its thread.</summary>
public sealed record SmsPhone(
    string Number,
    int Count,
    int Unread,
    int Senders,
    string LastMessage,
    string LastFromName,
    DateTimeOffset FirstReceivedAt,
    DateTimeOffset LastReceivedAt);

/// <summary>Totals across every phone.</summary>
public sealed record SmsStats(int Phones, int Messages, int Unread, int Senders, int Last24Hours);

[JsonSerializable(typeof(List<SmsMessage>))]
internal sealed partial class SmsJsonContext : JsonSerializerContext;

/// <summary>
/// Captured SMS, grouped by recipient number. Nothing is ever sent to a carrier.
/// Persisted as JSON in <c>DataDir</c> for SQLite storage, like <see cref="Templates.TemplateService"/>.
/// </summary>
public sealed class SmsService
{
    public const int MaxMessages = 10_000;
    public const int MaxBodyLength = 1600;

    private readonly Lock _gate = new();
    private readonly IMailEventBus _bus;
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly SmsOptions _options;
    private List<SmsMessage> _messages = [];

    public SmsService(IOptions<MailsinkOptions> options, IMailEventBus bus, TimeProvider time)
    {
        _bus = bus;
        _time = time;
        _options = options.Value.Sms;
        if (options.Value.Storage == StorageKind.Sqlite)
        {
            _path = Path.Combine(Path.GetFullPath(options.Value.DataDir), "sms.json");
            if (File.Exists(_path))
            {
                _messages = JsonSerializer.Deserialize(File.ReadAllText(_path), SmsJsonContext.Default.ListSmsMessage) ?? [];
            }
        }
    }

    /// <summary>
    /// Loose phone number check: strips spaces, dashes, dots and parentheses, allows one leading <c>+</c>,
    /// then requires 7–15 digits (the E.164 maximum). The result is the phone's key.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var builder = new StringBuilder(input.Length);
        var trimmed = input.Trim();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (c == '+' && i == 0)
            {
                builder.Append(c);
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
            {
                return false;
            }
        }

        var digits = builder.Length - (builder.Length > 0 && builder[0] == '+' ? 1 : 0);
        if (digits is < 7 or > 15)
        {
            return false;
        }

        normalized = builder.ToString();
        return true;
    }

    /// <summary>The sender used when a request leaves out the name or number. <paramref name="number"/> must already be normalized.</summary>
    public SmsSender ResolveSender(string? name, string? number) => new(
        string.IsNullOrWhiteSpace(name) ? _options.DefaultSenderName : name.Trim(),
        string.IsNullOrWhiteSpace(number) ? DefaultSenderNumber : number);

    private string DefaultSenderNumber => TryNormalize(_options.DefaultSenderNumber, out var n) ? n : _options.DefaultSenderNumber;

    /// <summary>Stores one message per (already normalized, distinct) recipient and publishes <see cref="SmsReceivedEvent"/>.</summary>
    public IReadOnlyList<SmsMessage> Send(SmsSender from, string body, IReadOnlyList<string> recipients)
    {
        var now = _time.GetUtcNow();
        var created = recipients.Select(to => new SmsMessage(Guid.NewGuid(), to, from.Name, from.Number, body, now, false)).ToList();
        lock (_gate)
        {
            _messages = [.. _messages, .. created];
            if (_messages.Count > MaxMessages)
            {
                _messages = [.. _messages.Skip(_messages.Count - MaxMessages)];
            }

            Persist();
        }

        _bus.Publish(new SmsReceivedEvent(created));
        return created;
    }

    public IReadOnlyList<SmsPhone> ListPhones()
    {
        lock (_gate)
        {
            return
            [
                .. _messages
                    .GroupBy(m => m.To)
                    .Select(g =>
                    {
                        // _messages is append-ordered, so First/Last are the oldest/newest even when timestamps tie.
                        var last = g.Last();
                        return new SmsPhone(g.Key, g.Count(), g.Count(m => !m.IsRead), g.Select(m => (m.FromName, m.FromNumber)).Distinct().Count(),
                            last.Body, last.FromName, g.First().ReceivedAt, last.ReceivedAt);
                    })
                    .OrderByDescending(p => p.LastReceivedAt),
            ];
        }
    }

    public SmsStats GetStats()
    {
        var since = _time.GetUtcNow().AddHours(-24);
        lock (_gate)
        {
            return new SmsStats(
                _messages.Select(m => m.To).Distinct().Count(),
                _messages.Count,
                _messages.Count(m => !m.IsRead),
                _messages.Select(m => (m.FromName, m.FromNumber)).Distinct().Count(),
                _messages.Count(m => m.ReceivedAt >= since));
        }
    }

    /// <summary>Messages to <paramref name="number"/>, oldest first. The number is normalized first.</summary>
    public IReadOnlyList<SmsMessage> GetThread(string number)
    {
        var key = Key(number);
        lock (_gate)
        {
            return [.. _messages.Where(m => m.To == key)];
        }
    }

    public int MarkRead(string number)
    {
        var key = Key(number);
        int changed;
        lock (_gate)
        {
            changed = _messages.Count(m => m.To == key && !m.IsRead);
            if (changed == 0)
            {
                return 0;
            }

            _messages = [.. _messages.Select(m => m.To == key ? m with { IsRead = true } : m)];
            Persist();
        }

        _bus.Publish(new SmsUpdatedEvent(key));
        return changed;
    }

    public bool DeletePhone(string number)
    {
        var key = Key(number);
        lock (_gate)
        {
            var before = _messages.Count;
            _messages = [.. _messages.Where(m => m.To != key)];
            if (_messages.Count == before)
            {
                return false;
            }

            Persist();
        }

        _bus.Publish(new SmsDeletedEvent(key));
        return true;
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            _messages = [];
            Persist();
        }

        _bus.Publish(new SmsDeletedEvent(null));
    }

    private static string Key(string number) => TryNormalize(number, out var normalized) ? normalized : number;

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_messages, SmsJsonContext.Default.ListSmsMessage));
    }
}

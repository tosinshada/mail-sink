using Microsoft.Extensions.Options;
using Mailsink.Core.Options;

namespace Mailsink.Core.Diagnostics;

public enum TranscriptDirection { Client, Server, Info }

public sealed record TranscriptLine(DateTimeOffset At, TranscriptDirection Direction, string Text);

public sealed record TranscriptSummary(
    string Id,
    string Protocol,
    string? ClientIp,
    string? User,
    bool Secure,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int Commands,
    IReadOnlyList<string> MessageIds);

/// <summary>A protocol session's C:/S: wire log. Credentials are redacted by the protocol layer before logging.</summary>
public sealed class SessionTranscript
{
    private readonly Lock _gate = new();
    private readonly List<TranscriptLine> _lines = [];
    private readonly List<string> _messageIds = [];
    private readonly int _maxLines;
    private int _commands;
    private bool _truncated;

    internal SessionTranscript(string protocol, string? clientIp, int maxLines)
    {
        Protocol = protocol;
        ClientIp = clientIp;
        _maxLines = maxLines;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Protocol { get; }
    public string? ClientIp { get; }
    public string? User { get; set; }
    public bool Secure { get; set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; private set; }

    public void Client(string text)
    {
        Interlocked.Increment(ref _commands);
        Add(TranscriptDirection.Client, text);
    }

    public void Server(string text) => Add(TranscriptDirection.Server, text);

    public void Info(string text) => Add(TranscriptDirection.Info, text);

    public void LinkMessage(string messageId)
    {
        lock (_gate)
        {
            _messageIds.Add(messageId);
        }
    }

    public void End() => EndedAt ??= DateTimeOffset.UtcNow;

    public IReadOnlyList<TranscriptLine> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public TranscriptSummary ToSummary()
    {
        lock (_gate)
        {
            return new TranscriptSummary(Id, Protocol, ClientIp, User, Secure, StartedAt, EndedAt, _commands, [.. _messageIds]);
        }
    }

    private void Add(TranscriptDirection direction, string text)
    {
        if (text.Length > 2000)
        {
            text = text[..2000] + "…";
        }

        lock (_gate)
        {
            if (_lines.Count >= _maxLines)
            {
                if (!_truncated)
                {
                    _truncated = true;
                    _lines.Add(new TranscriptLine(DateTimeOffset.UtcNow, TranscriptDirection.Info, "(transcript truncated)"));
                }

                return;
            }

            _lines.Add(new TranscriptLine(DateTimeOffset.UtcNow, direction, text));
        }
    }
}

/// <summary>Ring buffer of the most recent protocol sessions.</summary>
public sealed class TranscriptStore(IOptions<MailsinkOptions> options)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<SessionTranscript> _sessions = new();

    public SessionTranscript Start(string protocol, string? clientIp)
    {
        var diagnostics = options.Value.Diagnostics;
        var transcript = new SessionTranscript(protocol, clientIp, Math.Max(10, diagnostics.TranscriptLinesPerSession));
        lock (_gate)
        {
            _sessions.AddFirst(transcript);
            while (_sessions.Count > Math.Max(1, diagnostics.TranscriptSessions))
            {
                _sessions.RemoveLast();
            }
        }

        return transcript;
    }

    public IReadOnlyList<SessionTranscript> List()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }

    public SessionTranscript? Find(string id)
    {
        lock (_gate)
        {
            return _sessions.FirstOrDefault(s => s.Id == id);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessions.Clear();
        }
    }
}

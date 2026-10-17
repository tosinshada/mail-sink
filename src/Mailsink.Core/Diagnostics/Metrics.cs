using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Mailsink.Core.Diagnostics;

public sealed record MetricPoint(DateTimeOffset Minute, int Messages);

public sealed record MetricsSnapshot(
    DateTimeOffset StartedAt,
    long MessagesReceived,
    IReadOnlyDictionary<string, int> ActiveSessions,
    IReadOnlyDictionary<string, long> TotalSessions,
    IReadOnlyList<MetricPoint> MessagesPerMinute);

/// <summary>
/// Process-wide counters. Published through <see cref="System.Diagnostics.Metrics"/> (meter <c>Mailsink</c>) for
/// OpenTelemetry collection, and kept in a 60-minute window for the UI's Metrics page.
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    public const string MeterName = "Mailsink";
    private const int WindowMinutes = 60;

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _messages;
    private readonly Histogram<double> _commandDuration;
    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _total = new(StringComparer.OrdinalIgnoreCase);
    private readonly int[] _buckets = new int[WindowMinutes];
    private readonly long[] _bucketMinute = new long[WindowMinutes];
    private readonly Lock _gate = new();
    private long _messagesReceived;

    public MetricsCollector()
    {
        _messages = _meter.CreateCounter<long>("mailsink.smtp.messages_received", description: "Messages captured");
        _commandDuration = _meter.CreateHistogram<double>("mailsink.imap.command_duration", "ms", "IMAP command duration");
        _meter.CreateObservableGauge("mailsink.imap.sessions_active", () => _active.GetValueOrDefault("imap"));
        _meter.CreateObservableGauge("mailsink.smtp.sessions_active", () => _active.GetValueOrDefault("smtp"));
    }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void MessageReceived()
    {
        Interlocked.Increment(ref _messagesReceived);
        _messages.Add(1);
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var slot = (int)(minute % WindowMinutes);
        lock (_gate)
        {
            if (_bucketMinute[slot] != minute)
            {
                _bucketMinute[slot] = minute;
                _buckets[slot] = 0;
            }

            _buckets[slot]++;
        }
    }

    public void RecordCommand(string command, TimeSpan duration) =>
        _commandDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("command", command));

    public IDisposable TrackSession(string protocol)
    {
        _active.AddOrUpdate(protocol, 1, (_, v) => v + 1);
        _total.AddOrUpdate(protocol, 1, (_, v) => v + 1);
        return new SessionScope(this, protocol);
    }

    public MetricsSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var points = new List<MetricPoint>(WindowMinutes);
        lock (_gate)
        {
            for (var m = now - WindowMinutes + 1; m <= now; m++)
            {
                var slot = (int)(m % WindowMinutes);
                points.Add(new MetricPoint(DateTimeOffset.FromUnixTimeSeconds(m * 60), _bucketMinute[slot] == m ? _buckets[slot] : 0));
            }
        }

        return new MetricsSnapshot(StartedAt, Interlocked.Read(ref _messagesReceived),
            new Dictionary<string, int>(_active), new Dictionary<string, long>(_total), points);
    }

    public void Dispose() => _meter.Dispose();

    private sealed class SessionScope(MetricsCollector owner, string protocol) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner._active.AddOrUpdate(protocol, 0, (_, v) => Math.Max(0, v - 1));
            }
        }
    }
}

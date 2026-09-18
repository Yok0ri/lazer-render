using LazerRender.Api.Services.Logging;
using LazerRender.Contracts;

namespace LazerRender.Api.Services;

/// <summary>
/// The Phase 8.3 lifecycle layer over the two Phase 8.2 ring buffers. The SPA is deliberately
/// dependency-free (no SignalR client is shipped and the CSP forbids a CDN), so the panel polls while
/// it is open and the service tracks that with a simple lease: each poll refreshes the stream's
/// last-seen time, and a closed panel calls <see cref="Release"/>. <see cref="ClearIdle"/> is the
/// backstop for a tab that vanished without calling close.
///
/// The consequence is the retention contract from Roadmap §8.3: log lines exist only while an admin is
/// watching, and a stream nobody has polled is emptied.
/// </summary>
public sealed class LogStreamService
{
    private readonly object gate = new();
    private readonly Dictionary<LogSource, DateTimeOffset> lastPoll = new()
    {
        [LogSource.Service] = DateTimeOffset.MinValue,
        [LogSource.Engine] = DateTimeOffset.MinValue,
    };

    private readonly ServiceLogRingBuffer service;
    private readonly EngineLogRingBuffer engine;

    public LogStreamService(ServiceLogRingBuffer service, EngineLogRingBuffer engine)
    {
        this.service = service;
        this.engine = engine;
    }

    /// <summary>
    /// Returns records newer than <paramref name="afterSequence"/> and marks the stream as actively
    /// watched. <see cref="LogSnapshotDto.Cleared"/> tells the caller its cursor is stale (the buffer
    /// was cleared or wrapped) so it can reset instead of silently skipping lines.
    /// </summary>
    public LogSnapshotDto Poll(LogSource source, long afterSequence)
    {
        RingLogBuffer buffer = Buffer(source);

        lock (gate)
            lastPoll[source] = DateTimeOffset.UtcNow;

        IReadOnlyList<LogRecord> records = buffer.Snapshot(afterSequence);

        bool cleared = afterSequence > 0
                       && records.Count > 0
                       && records[0].Sequence > afterSequence + 1;

        return new LogSnapshotDto(
            records,
            buffer.DroppedCount,
            buffer.Capacity,
            buffer.MinimumSeverity.ToString().ToLowerInvariant(),
            cleared);
    }

    /// <summary>Stops watching a stream and empties it immediately (the panel's Close action).</summary>
    public void Release(LogSource source)
    {
        lock (gate)
            lastPoll[source] = DateTimeOffset.MinValue;

        Buffer(source).Clear();
    }

    public void ReleaseAll()
    {
        Release(LogSource.Service);
        Release(LogSource.Engine);
    }

    /// <summary>
    /// Clears any stream not polled within <paramref name="idle"/>. Called periodically by
    /// <see cref="LogRetentionService"/> so a panel that disappears without closing retains nothing.
    /// </summary>
    public void ClearIdle(TimeSpan idle)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (gate)
        {
            foreach (LogSource source in new[] { LogSource.Service, LogSource.Engine })
            {
                if (now - lastPoll[source] < idle)
                    continue;

                RingLogBuffer buffer = Buffer(source);

                if (buffer.Count > 0)
                    buffer.Clear();
            }
        }
    }

    /// <summary>Parses the wire name of a stream. Unknown values are rejected by the endpoint.</summary>
    public static bool TryParseSource(string? value, out LogSource source)
    {
        if (string.Equals(value, "service", StringComparison.OrdinalIgnoreCase))
        {
            source = LogSource.Service;
            return true;
        }

        if (string.Equals(value, "engine", StringComparison.OrdinalIgnoreCase))
        {
            source = LogSource.Engine;
            return true;
        }

        source = default;
        return false;
    }

    private RingLogBuffer Buffer(LogSource source) =>
        source == LogSource.Engine ? engine : service;
}

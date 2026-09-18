using LazerRender.Contracts;

namespace LazerRender.Api.Services.Logging;

/// <summary>
/// A bounded, thread-safe, in-memory ring buffer of log records. This is the "bounded in-memory ring
/// buffer for the service stream" of Phase 8.2 and, instantiated a second time, the engine stream of
/// Phase 8.3 — both are plain <see cref="ILogSink"/>s so producers never need to know which stream
/// they are feeding.
///
/// Nothing is persisted: when the process exits the records are gone, and the fixed capacity means
/// memory use is constant regardless of how much a render logs.
/// </summary>
public class RingLogBuffer : ILogSink
{
    private readonly object gate = new();
    private readonly LogRecord?[] items;

    /// <summary>Index the next accepted record will occupy (wraps).</summary>
    private int nextIndex;

    /// <summary>Number of slots currently occupied (saturates at <see cref="Capacity"/>).</summary>
    private int count;

    /// <summary>Next sequence number to hand out; 1-based so 0 means "from the beginning".</summary>
    private long nextSequence = 1;

    private long dropped;

    private int subscribers;

    public RingLogBuffer(int capacity, LogSeverity minimumSeverity = LogSeverity.Debug)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");

        Capacity = capacity;
        MinimumSeverity = minimumSeverity;
        items = new LogRecord?[capacity];
    }

    public int Capacity { get; }

    /// <summary>Records below this severity are discarded instead of stored.</summary>
    public LogSeverity MinimumSeverity { get; }

    /// <summary>How many records this buffer has evicted over its lifetime (observability for the UI).</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    /// <summary>Whether any consumer is currently watching, used by Phase 8.3's capture gate.</summary>
    public int SubscriberCount => Volatile.Read(ref subscribers);

    /// <summary>
    /// Raised after a record is stored, so a future SignalR bridge can push without polling. Handlers
    /// are invoked outside the buffer lock and any handler that throws is isolated.
    /// </summary>
    public event Action<LogRecord>? EntryAppended;

    public void Write(LogSource source, LogSeverity severity, string message)
    {
        if (severity < MinimumSeverity)
            return;

        LogRecord stamped;

        lock (gate)
        {
            stamped = new LogRecord(
                nextSequence++,
                DateTimeOffset.UtcNow,
                source,
                severity,
                message);

            if (count == Capacity)
            {
                // The slot we are about to overwrite holds the oldest record.
                dropped++;
            }
            else
            {
                count++;
            }

            items[nextIndex] = stamped;
            nextIndex = (nextIndex + 1) % Capacity;
        }

        Notify(stamped);
    }

    /// <summary>
    /// Returns a consistent copy of the retained records, oldest first. When
    /// <paramref name="afterSequence"/> is non-zero only records with a higher sequence are returned,
    /// which is how a consumer polls incrementally.
    /// </summary>
    public IReadOnlyList<LogRecord> Snapshot(long afterSequence = 0)
    {
        lock (gate)
        {
            if (count == 0)
                return Array.Empty<LogRecord>();

            var result = new List<LogRecord>(count);
            int start = (nextIndex - count + Capacity) % Capacity;

            for (int i = 0; i < count; i++)
            {
                LogRecord record = items[(start + i) % Capacity]!;

                if (record.Sequence > afterSequence)
                    result.Add(record);
            }

            return result;
        }
    }

    /// <summary>
    /// Registers a push handler and raises <see cref="SubscriberCount"/>. Disposing the returned
    /// handle removes the handler and lowers the count. Phase 8.3 uses the count to decide whether to
    /// tee the engine's output at all.
    /// </summary>
    public IDisposable Subscribe(Action<LogRecord> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        EntryAppended += handler;
        Interlocked.Increment(ref subscribers);

        return new Subscription(this, handler);
    }

    /// <summary>Discards every retained record. Used when the last admin panel disconnects.</summary>
    public void Clear()
    {
        lock (gate)
        {
            Array.Clear(items);
            nextIndex = 0;
            count = 0;
        }
    }

    private void Notify(LogRecord record)
    {
        Action<LogRecord>? handlers = EntryAppended;
        if (handlers is null)
            return;

        // One subscriber must not be able to break the producer or its peers.
        foreach (Action<LogRecord> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(record);
            }
            catch
            {
                // A sink must never break the producer.
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private RingLogBuffer? owner;
        private readonly Action<LogRecord> handler;

        public Subscription(RingLogBuffer owner, Action<LogRecord> handler)
        {
            this.owner = owner;
            this.handler = handler;
        }

        public void Dispose()
        {
            RingLogBuffer? buffer = Interlocked.Exchange(ref owner, null);
            if (buffer is null)
                return;

            buffer.EntryAppended -= handler;
            Interlocked.Decrement(ref buffer.subscribers);
        }
    }
}
using LazerRender.Contracts;

namespace LazerRender.Api.Services.Logging;

/// <summary>
/// A destination for classified, redacted log records. Phase 8.2's pipeline fans every record out to
/// one or more sinks; the ring buffers are sinks, and so is the console bridge. New destinations
/// (a disk sink, a future exporter) are added by registering another implementation rather than by
/// changing the producers.
/// </summary>
public interface ILogSink
{
    /// <summary>
    /// Writes one record. The sink is responsible for stamping its own sequence number and timestamp;
    /// callers pass only source, severity and the already-redacted, already-capped text.
    ///
    /// Implementations must never throw: a misbehaving sink must not break the producer (a logger or
    /// the render runner).
    /// </summary>
    void Write(LogSource source, LogSeverity severity, string message);
}
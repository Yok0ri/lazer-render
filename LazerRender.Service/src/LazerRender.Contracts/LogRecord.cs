namespace LazerRender.Contracts;

/// <summary>
/// Which producer a log record came from. The admin console (Phase 8.3) renders the two as
/// independent streams, so the distinction is part of the wire contract rather than a UI detail.
/// </summary>
public enum LogSource
{
    /// <summary>This API/worker process (ASP.NET Core, EF Core, the render worker).</summary>
    Service,

    /// <summary>The recorder child process, teed from its stdout/stderr by the render runner.</summary>
    Engine,
}

/// <summary>
/// Severity of a log record. Deliberately our own enum rather than
/// <c>Microsoft.Extensions.Logging.LogLevel</c>: <c>LazerRender.Contracts</c> has no package
/// references, and this is the shape the browser deserializes.
/// </summary>
public enum LogSeverity
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>
/// The single log record model shared by every sink of the Phase 8.2 pipeline. The service ring
/// buffer, the engine ring buffer and any future sink all emit this shape, so the Phase 8.3 admin
/// panel and the local debug workflow consume one model instead of reimplementing classification.
/// </summary>
/// <param name="Sequence">
/// Monotonic, per-buffer sequence number (1-based). Consumers poll with the last sequence they saw to
/// receive only new records; it is not globally unique across buffers.
/// </param>
/// <param name="Timestamp">UTC time the record was written to its buffer.</param>
/// <param name="Source">Service or engine.</param>
/// <param name="Severity">Classified severity.</param>
/// <param name="Message">Redacted, length-capped text. Never contains a credential.</param>
public sealed record LogRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    LogSource Source,
    LogSeverity Severity,
    string Message);
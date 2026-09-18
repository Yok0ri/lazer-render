using LazerRender.Contracts;
using Microsoft.Extensions.Logging;

namespace LazerRender.Api.Services.Logging;

/// <summary>
/// The one place engine stdout/stderr is classified, redacted, bounded and fanned out. Extracted from
/// the render runner so that the Phase 8.3 admin console and the local debug workflow are consumers of
/// the same pipeline rather than reimplementations (Roadmap §8.2).
///
/// The three-level classification is preserved exactly as it was in
/// <c>RendererProcessRunner.logEngineLine</c>: problem markers at Warning, notable markers at
/// Information, everything else at Debug. Every line is redacted and length-capped before either the
/// <see cref="ILogger"/> or a sink sees it, and the whole render shares a byte budget so a broken or
/// adversarial upload cannot flood the log.
/// </summary>
public sealed class EngineLogForwarder
{
    /// <summary>Engine log lines worth surfacing at the default level. Everything else is Debug.</summary>
    private static readonly string[] NotableMarkers =
    {
        "Leaderboard:", "osu! API login:", "Avatar:", "Replay complete;", "Recorded ",
        "treating it as ranked", "HUD visibility", "HudVisibilityFilter", "failure",
    };

    /// <summary>Engine lines that indicate something went wrong, surfaced as warnings.</summary>
    private static readonly string[] ProblemMarkers =
    {
        "error", "exception", "failed", "failure", "unhandled", "fatal", "crash", "denied",
    };

    /// <summary>Upper bound on a single forwarded engine log line.</summary>
    public const int MaxLoggedLineLength = 2000;

    /// <summary>
    /// Upper bound on how much engine output one render may push into the sinks. Engine output is
    /// derived from user-supplied inputs, so a broken upload could otherwise emit unbounded text.
    /// </summary>
    public const long MaxLoggedBytesPerRun = 256 * 1024;

    private readonly ILogger logger;
    private readonly LogRedactor redactor;
    private readonly RingLogBuffer? buffer;

    public EngineLogForwarder(ILogger logger, LogRedactor redactor, RingLogBuffer? buffer = null)
    {
        this.logger = logger;
        this.redactor = redactor;
        this.buffer = buffer;
    }

    /// <summary>
    /// Forwards one line of the engine's own log. <paramref name="stream"/> is <c>stdout</c> or
    /// <c>stderr</c>, kept as the source detail in the "[engine/...]" prefix and in the sink record's
    /// message.
    /// </summary>
    public void Forward(string stream, string line, IReadOnlyList<string> secrets, LogBudget budget)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        line = redactor.Redact(line, secrets);

        if (line.Length > MaxLoggedLineLength)
            line = string.Concat(line.AsSpan(0, MaxLoggedLineLength), "…[truncated]");

        if (!budget.TryReserve(line.Length))
        {
            if (budget.NotifyExhaustedOnce())
            {
                logger.LogWarning(
                    "[engine/{Stream}] further engine output suppressed for this render (budget {Budget} bytes reached).",
                    stream, MaxLoggedBytesPerRun);
            }

            return;
        }

        LogSeverity severity = Classify(line);

        switch (severity)
        {
            case LogSeverity.Warning:
                logger.LogWarning("[engine/{Stream}] {Line}", stream, line);
                break;
            case LogSeverity.Information:
                logger.LogInformation("[engine/{Stream}] {Line}", stream, line);
                break;
            default:
                logger.LogDebug("[engine/{Stream}] {Line}", stream, line);
                break;
        }

        // The sink record keeps the stream in the message so the two engine pipes remain distinguishable
        // in the admin console without an extra field.
        buffer?.Write(LogSource.Engine, severity, $"[{stream}] {line}");
    }

    /// <summary>
    /// Classifies a (already redacted) line. Exposed for tests: this is the behaviour the admin panel's
    /// severity colouring depends on.
    /// </summary>
    internal static LogSeverity Classify(string line)
    {
        if (ProblemMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return LogSeverity.Warning;

        if (NotableMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return LogSeverity.Information;

        return LogSeverity.Debug;
    }
}

/// <summary>Per-render byte budget for forwarded engine output.</summary>
public sealed class LogBudget
{
    private readonly long limit;
    private long remaining;
    private int notified;

    public LogBudget(long limit)
    {
        this.limit = limit;
        remaining = limit;
    }

    public long Limit => limit;

    /// <summary>Reserves room for a line; false once the budget is exhausted.</summary>
    public bool TryReserve(int bytes) => Interlocked.Add(ref remaining, -bytes) > 0;

    /// <summary>True exactly once, so the "output suppressed" notice is logged a single time.</summary>
    public bool NotifyExhaustedOnce() => Interlocked.Exchange(ref notified, 1) == 0;
}
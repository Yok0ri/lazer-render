using LazerRender.Contracts;

namespace LazerRender.Api.Configuration;

/// <summary>
/// Bounds and levels for the in-memory log pipeline (Phase 8.2). Nothing here is persisted: the
/// ring buffers are the only store and they live entirely in process memory, which is what keeps
/// the "stream only while an admin is watching" contract of Phase 8.3 possible.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Number of service log records retained. At ~200 bytes each, 500 is ~100 KB.</summary>
    public int ServiceBufferSize { get; set; } = 500;

    /// <summary>
    /// Number of engine log records retained. The engine is far chattier, so the default is larger;
    /// combined with <see cref="EngineMinimumLevel"/> the buffer holds the render's interesting tail.
    /// </summary>
    public int EngineBufferSize { get; set; } = 1000;

    /// <summary>
    /// Minimum severity captured for the service stream. Default Information; the LAZERRENDER_DEBUG
    /// runtime toggle (and a Debug build) lowers it to Debug.
    /// </summary>
    public string ServiceMinimumLevel { get; set; } = "Information";

    /// <summary>
    /// Minimum severity captured for the engine stream. Default Information: the engine's framework
    /// chatter is Debug and would otherwise evict every notable line within seconds of a render.
    /// </summary>
    public string EngineMinimumLevel { get; set; } = "Information";

    /// <summary>Parses a configured level, falling back to <paramref name="fallback"/> on garbage.</summary>
    public static LogSeverity ParseSeverity(string? value, LogSeverity fallback)
    {
        if (Enum.TryParse<LogSeverity>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;

        return fallback;
    }
}
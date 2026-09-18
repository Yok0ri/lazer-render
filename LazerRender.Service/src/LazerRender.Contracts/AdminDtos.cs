namespace LazerRender.Contracts;

/// <summary>
/// One page of a console-log stream (Phase 8.3). The panel polls with the highest
/// <see cref="LogRecord.Sequence"/> it has already rendered and appends the delta.
/// </summary>
/// <param name="Records">Records newer than the requested sequence, oldest first.</param>
/// <param name="Dropped">How many records the ring buffer has evicted over its lifetime.</param>
/// <param name="Capacity">The buffer's fixed capacity (so the UI can show "last N lines").</param>
/// <param name="MinimumSeverity">Effective capture floor, e.g. <c>information</c> or <c>debug</c>.</param>
/// <param name="Cleared">
/// True when the requested sequence is older than everything retained, i.e. the buffer was cleared or
/// wrapped while the panel was away. The UI should reset its accumulated lines.
/// </param>
public sealed record LogSnapshotDto(
    IReadOnlyList<LogRecord> Records,
    long Dropped,
    int Capacity,
    string MinimumSeverity,
    bool Cleared);

/// <summary>
/// The admin "Render PC" card (Phase 8.3): a best-effort hardware/software summary of the machine that
/// actually performs renders. Every probe is optional; a field is null when it could not be read rather
/// than failing the whole card.
/// </summary>
public sealed record RenderPcDto(
    string OperatingSystem,
    string DotnetRuntime,
    string CpuModel,
    int CpuCores,
    long? MemoryTotalBytes,
    string? Gpu,
    string? GpuDriver,
    string? FfmpegVersion,
    string Encoder,
    bool EncoderAutoDetected,
    string ResultsPath,
    long? ResultsFreeBytes,
    long? ResultsTotalBytes,
    DateTimeOffset CollectedAt);

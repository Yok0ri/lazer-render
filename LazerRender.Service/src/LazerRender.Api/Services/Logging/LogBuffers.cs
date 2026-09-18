using LazerRender.Api.Configuration;
using LazerRender.Contracts;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services.Logging;

/// <summary>
/// The service-stream ring buffer: everything this process logs through <c>ILogger</c>. Registered as
/// a distinct type so DI can hand it to the logger provider without colliding with the engine buffer.
/// </summary>
public sealed class ServiceLogRingBuffer : RingLogBuffer
{
    public ServiceLogRingBuffer(IOptions<ObservabilityOptions> options)
        : base(
            options.Value.ServiceBufferSize,
            EffectiveLevel(options.Value.ServiceMinimumLevel, LogSeverity.Information))
    {
    }

    private static LogSeverity EffectiveLevel(string? configured, LogSeverity fallback) =>
        DebugMode.Enabled ? LogSeverity.Debug : ObservabilityOptions.ParseSeverity(configured, fallback);
}

/// <summary>
/// The engine-stream ring buffer: the recorder child process's stdout/stderr, teed by
/// <see cref="EngineLogForwarder"/>. A separate instance (rather than one shared buffer) is what lets
/// the Phase 8.3 panel show the two streams independently and keeps framework chatter from evicting
/// service records.
/// </summary>
public sealed class EngineLogRingBuffer : RingLogBuffer
{
    public EngineLogRingBuffer(IOptions<ObservabilityOptions> options)
        : base(
            options.Value.EngineBufferSize,
            EffectiveLevel(options.Value.EngineMinimumLevel, LogSeverity.Information))
    {
    }

    private static LogSeverity EffectiveLevel(string? configured, LogSeverity fallback) =>
        DebugMode.Enabled ? LogSeverity.Debug : ObservabilityOptions.ParseSeverity(configured, fallback);
}
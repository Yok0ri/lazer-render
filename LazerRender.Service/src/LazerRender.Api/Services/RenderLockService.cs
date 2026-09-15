namespace LazerRender.Api.Services;

/// <summary>
/// Serializes all LazerRender invocations (renders and asset imports) because the engine is only
/// proven safe in single-operation mode and both share one Realm storage directory.
/// </summary>
public sealed class RenderLockService
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

using Microsoft.Extensions.Hosting;

namespace LazerRender.Api.Services;

/// <summary>
/// Periodically empties a log stream nobody has polled recently. This is the backstop for the Phase 8.3
/// retention contract: the panel's Close action calls <see cref="LogStreamService.Release"/> directly,
/// but a tab that is killed or loses its connection cannot, so an idle stream is cleared here.
/// </summary>
public sealed class LogRetentionService : BackgroundService
{
    /// <summary>How often to check. Short enough to look immediate, cheap enough to be invisible.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    /// <summary>A stream is dropped once this long has passed without a poll.</summary>
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(20);

    private readonly LogStreamService stream;

    public LogRetentionService(LogStreamService stream)
    {
        this.stream = stream;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                stream.ClearIdle(IdleThreshold);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }
}

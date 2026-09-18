using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LazerRender.Api.Services;

/// <summary>
/// Fills the Render PC cache once at startup so the first admin request is instant and no probe ever
/// runs on the request path. Failures are logged and ignored: the panel can always trigger a manual
/// refresh, and a missing probe must not stop the host.
/// </summary>
public sealed class SystemInfoWarmupService : BackgroundService
{
    private readonly SystemInfoService systemInfo;
    private readonly ILogger<SystemInfoWarmupService> logger;

    public SystemInfoWarmupService(SystemInfoService systemInfo, ILogger<SystemInfoWarmupService> logger)
    {
        this.systemInfo = systemInfo;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await systemInfo.GetAsync(refresh: true, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Render PC summary could not be collected at startup; the panel can refresh it later.");
        }
    }
}

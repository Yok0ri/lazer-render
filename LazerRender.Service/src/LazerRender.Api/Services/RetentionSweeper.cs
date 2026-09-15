using LazerRender.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Services;

/// <summary>
/// Periodically deletes render results whose retention window has elapsed. The job row and its
/// metadata are kept for history; only the deliverable file and its result directory are removed.
/// </summary>
public sealed class RetentionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<RetentionSweeper> logger;

    public RetentionSweeper(IServiceScopeFactory scopeFactory, ILogger<RetentionSweeper> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Retention sweep failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow;
        var expired = await db.Jobs
            .Where(j => j.OutputPath != null && j.RetainedUntil != null && j.RetainedUntil < now)
            .ToListAsync(ct);

        foreach (var job in expired)
        {
            if (job.OutputPath is not null && File.Exists(job.OutputPath))
            {
                File.Delete(job.OutputPath);

                var resultDirectory = Path.GetDirectoryName(job.OutputPath);
                if (resultDirectory is not null &&
                    Directory.Exists(resultDirectory) &&
                    !Directory.EnumerateFileSystemEntries(resultDirectory).Any())
                {
                    Directory.Delete(resultDirectory);
                }
            }

            job.OutputPath = null;
            job.ResultSize = null;
        }

        await db.SaveChangesAsync(ct);

        if (expired.Count > 0)
            logger.LogInformation("Retention sweep removed {Count} expired result(s).", expired.Count);
    }
}

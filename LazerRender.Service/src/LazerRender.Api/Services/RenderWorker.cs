using System.Text.Json;
using LazerRender.Api.Configuration;
using LazerRender.Api.Data;
using LazerRender.Api.Hubs;
using LazerRender.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// The single render worker. It claims queued jobs atomically from SQLite, supervises one
/// LazerRender child process at a time, streams progress into the database and SignalR, and
/// handles cancellation, retries and result finalization.
/// </summary>
public sealed class RenderWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly RendererProcessRunner runner;
    private readonly JobCancellationService cancellation;
    private readonly IHubContext<JobsHub> hub;
    private readonly RendererOptions rendererOptions;
    private readonly AssetImportRunner assetRunner;
    private readonly RenderSizeEstimator sizeEstimator;
    private readonly MapMetadataService mapMetadata;
    private readonly OsuBotAuthService osuBotAuth;
    private readonly UserOsuTokenService userOsuTokens;
    private readonly ILogger<RenderWorker> logger;

    // One active render at a time (single GPU). Future multi-GPU: one worker per GPU, each with
    // its own gate, claiming from the same table.
    private readonly RenderLockService renderLock;

    public RenderWorker(
        IServiceScopeFactory scopeFactory,
        RendererProcessRunner runner,
        JobCancellationService cancellation,
        IHubContext<JobsHub> hub,
        IOptions<RendererOptions> rendererOptions,
        RenderLockService renderLock,
        AssetImportRunner assetRunner,
        RenderSizeEstimator sizeEstimator,
        MapMetadataService mapMetadata,
        OsuBotAuthService osuBotAuth,
        UserOsuTokenService userOsuTokens,
        ILogger<RenderWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.cancellation = cancellation;
        this.hub = hub;
        this.rendererOptions = rendererOptions.Value;
        this.renderLock = renderLock;
        this.assetRunner = assetRunner;
        this.sizeEstimator = sizeEstimator;
        this.mapMetadata = mapMetadata;
        this.osuBotAuth = osuBotAuth;
        this.userOsuTokens = userOsuTokens;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverStaleJobsAsync(stoppingToken);

        // Backfill metadata for jobs whose queue-time lookup failed before the post-render
        // re-resolve existed (their maps are usually in the database by now).
        _ = Task.Run(() => BackfillMissingMetadataAsync(stoppingToken));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string? jobId;
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    jobId = await ClaimNextAsync(db, stoppingToken);
                }

                if (jobId is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                // Hold the shared lock only for the duration of the actual LazerRender
                // invocation, so asset imports can acquire it while the worker idles.
                await renderLock.Gate.WaitAsync(stoppingToken);
                try
                {
                    await RunJobAsync(jobId, stoppingToken);
                }
                finally
                {
                    renderLock.Gate.Release();
                }

                // The render may have downloaded and imported the replay's beatmap
                // (--download-missing). If the job was queued before that import, its queue-time
                // metadata lookup failed and job cards fall back to "Replay by <player>".
                // Re-resolve now that the map is in the database. Fire-and-forget: the worker
                // loop continues to the next job while metadata catches up.
                _ = Task.Run(() => ReResolveMetadataAsync(jobId));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Render worker iteration failed.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Re-resolves metadata for existing jobs that never got it (oldest-first cap of 50 per
    /// startup). Sequential so the shared render lock is only ever requested one at a time.
    /// </summary>
    private async Task BackfillMissingMetadataAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var jobIds = await db.Jobs.AsNoTracking()
                .Where(j => j.MapTitle == null && j.ReplayMd5 != null)
                .OrderByDescending(j => j.CreatedAt)
                .Take(50)
                .Select(j => j.Id)
                .ToListAsync(ct);

            foreach (var jobId in jobIds)
            {
                if (ct.IsCancellationRequested)
                    return;

                await ReResolveMetadataAsync(jobId);
            }

            if (jobIds.Count > 0)
                logger.LogInformation("Backfilled map metadata for {Count} job(s).", jobIds.Count);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Map metadata backfill failed.");
        }
    }

    /// <summary>
    /// Re-runs beatmap metadata resolution for a job whose queue-time lookup failed (the beatmap
    /// was not yet in the engine database). No-op when the metadata is already present.
    /// </summary>
    private async Task ReResolveMetadataAsync(string jobId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await db.Jobs.AsNoTracking()
                .Where(j => j.Id == jobId && j.MapTitle == null && j.ReplayMd5 != null)
                .Select(j => new { j.Id, j.ReplayMd5 })
                .FirstOrDefaultAsync();

            if (job is not null)
                await mapMetadata.ResolveAndUpdateAsync(job.Id, job.ReplayMd5!, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Post-render map metadata re-resolution failed for job {JobId}.", jobId);
        }
    }

    internal static async Task<string?> ClaimNextAsync(AppDbContext db, CancellationToken ct)
    {
        var jobId = await db.Jobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(ct);

        if (jobId is null)
            return null;

        // Hoist the timestamp into a local so EF parameterizes it. DateTimeOffset.UtcNow inside the
        // expression cannot be translated by the SQLite provider (it would try to evaluate it as SQL).
        var claimedAt = DateTimeOffset.UtcNow;

        var updated = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, JobStatus.Claimed)
                .SetProperty(j => j.ClaimedAt, claimedAt)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);

        return updated == 0 ? null : jobId;
    }

    private async Task RunJobAsync(string jobId, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<StorageService>();

        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null)
        {
            cancellation.Clear(jobId);
            return;
        }

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            cancellation.GetToken(jobId));

        try
        {
            job.Status = JobStatus.Rendering;
            job.StartedAt ??= DateTimeOffset.UtcNow;
            job.Phase = RenderPhase.Parsing.ToWire();
            await db.SaveChangesAsync(jobCts.Token);

            // Preparation: estimate the output size and reject jobs that would exceed the disk
            // budget before launching the render process. Best-effort — if --replay-info fails
            // (e.g. transient engine error) the job proceeds normally.
            string? sizeError = await CheckSizeLimitAsync(db, job, storage, jobCts.Token);
            if (sizeError is not null)
            {
                await MarkRejectedAsync(db, storage, job, sizeError);
                return;
            }

            // Persist the replay metadata gathered by the size check before the render starts.
            await db.SaveChangesAsync(jobCts.Token);

            await storage.WriteRenderConfigAsync(job.Id, job.RenderConfigJson, jobCts.Token);

            // Sign the engine into the osu! API with a user token so online beatmap leaderboards
            // work. Prefer the identity of the player who queued this render (their own stored
            // credential), then fall back to the configured bot credential. Best-effort: without
            // either, the render simply produces an offline scoreboard.
            OsuAccessToken? osuToken = await userOsuTokens.GetTokenAsync(job.OwnerUserId, jobCts.Token)
                                      ?? await osuBotAuth.GetTokenAsync(jobCts.Token);

            string? avatarApiKey = string.IsNullOrWhiteSpace(rendererOptions.AvatarApiKey)
                ? null
                : rendererOptions.AvatarApiKey;

            // The engine reads its credentials from an owner-only file rather than from the command
            // line, so a live token is never visible in `ps` / `/proc/<pid>/cmdline`. The values stay
            // here as well so the log bridge can redact an accidental echo.
            var redacted = new List<string>();
            string? secretsPath = null;

            if (osuToken is not null || avatarApiKey is not null)
            {
                secretsPath = storage.SecretsPath(job.Id);

                var secrets = new Dictionary<string, object?>();

                if (osuToken is not null)
                {
                    secrets[@"osuUserToken"] = osuToken.AccessToken;
                    secrets[@"osuUserTokenExpiresIn"] = osuToken.ExpiresIn;
                    redacted.Add(osuToken.AccessToken);
                }

                if (avatarApiKey is not null)
                {
                    secrets[@"avatarApiKey"] = avatarApiKey;
                    redacted.Add(avatarApiKey);
                }

                await storage.WriteSecretsAsync(job.Id, JsonSerializer.Serialize(secrets), jobCts.Token);
            }

            var invocation = new RenderInvocation(
                job.Id,
                storage.StagedReplayPath(job.Id),
                storage.RenderConfigPath(job.Id),
                storage.OutputDirectory(job.Id),
                storage.RealmDirectory,
                job.Encoder.ToString().ToLowerInvariant(),
                rendererOptions.DownloadMissing,
                secretsPath,
                redacted);

            RenderRunResult result;

            try
            {
                result = await runner.RunAsync(invocation, jobCts.Token, async progress =>
                {
                    try
                    {
                        job.Phase = progress.ParsedPhase.ToWire();
                        job.Frame = progress.Frame;
                        job.Total = progress.Total;
                        job.FpsNow = progress.Fps;

                        if (progress.ParsedPhase == RenderPhase.Finalizing)
                            job.Status = JobStatus.Finalizing;

                        await db.SaveChangesAsync();
                        await BroadcastProgressAsync(jobId, progress);
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "Failed to persist progress for job {JobId}.", jobId);
                    }
                });
            }
            finally
            {
                // The engine deletes the file once it has read it; this also covers a render that
                // never started. A credential must not outlive the job it was minted for.
                if (secretsPath is not null)
                    storage.DeleteSecrets(job.Id);
            }

            if (jobCts.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                await MarkCancelledAsync(db, storage, job);
                return;
            }

            if (!result.Success)
            {
                await MarkFailedAsync(db, job, "Render did not complete (no DONE progress line).");
                return;
            }

            await FinalizeSuccessAsync(db, storage, job);
        }
        catch (OperationCanceledException)
        {
            if (jobCts.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                await MarkCancelledAsync(db, storage, job);
            else
                await RequeueAsync(db, job);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Job {JobId} failed unexpectedly.", jobId);
            await MarkFailedAsync(db, job, e.Message);
        }
        finally
        {
            cancellation.Clear(jobId);
        }
    }

    private async Task BroadcastProgressAsync(string jobId, RenderProgress progress)
    {
        await hub.Clients.Group(jobId).SendAsync("progress", new
        {
            jobId,
            phase = progress.ParsedPhase.ToWire(),
            frame = progress.Frame,
            total = progress.Total,
            fps = progress.Fps,
        });
    }

    private async Task<string?> CheckSizeLimitAsync(AppDbContext db, JobEntity job, StorageService storage, CancellationToken ct)
    {
        try
        {
            var args = new List<string>
            {
                "--replay-info",
                storage.StagedReplayPath(job.Id),
                "--storage",
                storage.RealmDirectory,
            };

            if (rendererOptions.DownloadMissing)
                args.Add("--download-missing");

            string stdout = await assetRunner.CaptureAsync(args, ct);
            ReplayRenderInfo? info = RenderSizeEstimator.ParseReplayInfo(stdout);

            if (info is null || !info.Found)
                return null;

            // Replay-derived metadata for the job's extended view (song length, star rating, mods,
            // accuracy). Best-effort: the render proceeds even if these are unavailable.
            job.SongLength = info.SongLengthSeconds > 0 ? info.SongLengthSeconds : null;
            job.Mods = string.IsNullOrEmpty(info.Mods) ? null : info.Mods;
            job.Accuracy = info.Accuracy;

            if (job.MapStars is null && info.Stars > 0)
                job.MapStars = info.Stars;

            // The --replay-info call above has just ensured the beatmap is imported, so this is the
            // earliest point at which the job can show its real title. Apply it here rather than only
            // after the render: a queued job whose queue-time lookup lost the shared render lock used
            // to read "Replay by <player>" for its whole render duration.
            await ApplyAndCacheMapMetadataAsync(db, job, info);

            long estimate = sizeEstimator.EstimateBytes(job.Width, job.Height, job.Fps, info.DurationSeconds);
            long limit = sizeEstimator.LimitBytes;

            if (estimate <= limit)
            {
                logger.LogInformation(
                    "Job {JobId} size estimate {Estimate} within limit {Limit}.",
                    job.Id, FormatBytes(estimate), FormatBytes(limit));
                return null;
            }

            return $"Estimated output size {FormatBytes(estimate)} exceeds the {FormatBytes(limit)} internal limit " +
                   $"for this replay ({info.DurationSeconds:F1}s at {job.Width}x{job.Height}@{job.Fps} fps). " +
                   "Try a lower resolution, a lower frame rate or a shorter clip.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Size-limit check failed for job {JobId}; proceeding without it.", job.Id);
            return null;
        }
    }

    private async Task MarkRejectedAsync(AppDbContext db, StorageService storage, JobEntity job, string error)
    {
        job.Status = JobStatus.Rejected;
        job.ErrorMessage = error;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        storage.DeleteStagedReplay(job.Id);
        storage.DeleteJobDirectory(job.Id);
    }

    private static async Task ApplyAndCacheMapMetadataAsync(AppDbContext db, JobEntity job, ReplayRenderInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Title))
            return;

        if (string.IsNullOrEmpty(job.MapTitle))
        {
            job.MapTitle = info.Title;
            job.MapArtist = info.Artist ?? string.Empty;
            job.MapCreator = info.Creator ?? string.Empty;
            job.MapVersion = info.Version ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(job.ReplayMd5))
            return;

        // Seed the beatmap cache so the queue-time lookup for later jobs of the same map resolves
        // immediately instead of waiting on (and losing to) the render lock.
        var row = await db.BeatmapCache.SingleOrDefaultAsync(b => b.Md5 == job.ReplayMd5);
        if (row is null)
        {
            row = new BeatmapCacheEntity { Md5 = job.ReplayMd5 };
            db.BeatmapCache.Add(row);
        }

        row.Imported = true;
        row.DownloadedAt ??= DateTimeOffset.UtcNow;
        row.Title = info.Title;
        row.Artist = info.Artist ?? string.Empty;
        row.Creator = info.Creator ?? string.Empty;
        row.Version = info.Version ?? string.Empty;

        if (info.Stars > 0)
            row.Stars = info.Stars;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private async Task MarkCancelledAsync(AppDbContext db, StorageService storage, JobEntity job)
    {
        job.Status = JobStatus.Cancelled;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        storage.DeleteStagedReplay(job.Id);
        storage.DeleteJobDirectory(job.Id);
    }

    private async Task MarkFailedAsync(AppDbContext db, JobEntity job, string error)
    {
        if (job.Attempts < job.MaxAttempts)
        {
            job.Status = JobStatus.Queued;
            job.Phase = null;
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = error;
            job.FinishedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task RequeueAsync(AppDbContext db, JobEntity job)
    {
        if (job.Attempts < job.MaxAttempts)
        {
            job.Status = JobStatus.Queued;
            job.Phase = null;
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = "Cancelled during host shutdown.";
            job.FinishedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task FinalizeSuccessAsync(AppDbContext db, StorageService storage, JobEntity job)
    {
        var outputFile = Path.Combine(storage.OutputDirectory(job.Id), "output.mp4");
        if (!File.Exists(outputFile))
        {
            await MarkFailedAsync(db, job, "output.mp4 missing after render.");
            return;
        }

        var resultPath = storage.ResultPath(job.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        File.Move(outputFile, resultPath, overwrite: true);

        job.Status = JobStatus.Completed;
        job.Phase = RenderPhase.Done.ToWire();
        job.OutputPath = resultPath;
        job.ResultSize = new FileInfo(resultPath).Length;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        storage.DeleteStagedReplay(job.Id);
        storage.DeleteJobDirectory(job.Id);
    }

    private async Task RecoverStaleJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stale = await db.Jobs
            .Where(j => j.Status == JobStatus.Uploaded ||
                        j.Status == JobStatus.Claimed ||
                        j.Status == JobStatus.Rendering ||
                        j.Status == JobStatus.Finalizing ||
                        j.Status == JobStatus.Cancelling)
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            if (job.Status == JobStatus.Cancelling)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
            else if (job.Status == JobStatus.Uploaded)
            {
                // The service stopped while the job was being created (before it was queued).
                // The replay is staged, so it can be queued normally.
                job.Status = JobStatus.Queued;
            }
            else if (job.Attempts < job.MaxAttempts)
            {
                job.Status = JobStatus.Queued;
                job.Phase = null;
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.ErrorMessage = "Worker restarted while the job was active.";
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);

        if (stale.Count > 0)
            logger.LogInformation("Recovered {Count} stale job(s) after startup.", stale.Count);
    }
}

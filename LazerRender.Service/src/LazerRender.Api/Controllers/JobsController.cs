using System.Security.Claims;
using System.Text.Json;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using LazerRender.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Controllers;

[ApiController]
[Route("/api/v1/jobs")]
[Authorize]
public sealed class JobsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext db;
    private readonly StorageService storage;
    private readonly QuotaService quota;
    private readonly JobCancellationService cancellation;
    private readonly EncoderResolver encoderResolver;
    private readonly MapMetadataService mapMetadata;

    public JobsController(
        AppDbContext db,
        StorageService storage,
        QuotaService quota,
        JobCancellationService cancellation,
        EncoderResolver encoderResolver,
        MapMetadataService mapMetadata)
    {
        this.db = db;
        this.storage = storage;
        this.quota = quota;
        this.cancellation = cancellation;
        this.encoderResolver = encoderResolver;
        this.mapMetadata = mapMetadata;
    }

    [HttpPost]
    public async Task<ActionResult<JobCreatedResponse>> Create(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        var file = Request.Form.Files.GetFile("file");
        var config = Request.Form["config"].ToString();
        var skin = Request.Form["skin"].ToString();

        if (file is null || file.Length == 0)
            return BadRequest(new ErrorResponse("replay_required", "An .osr file must be uploaded."));

        if (!file.FileName.EndsWith(".osr", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ErrorResponse("invalid_extension", "Only .osr replay files are accepted."));

        if (file.Length > quota.MaxUploadBytes)
            return BadRequest(new ErrorResponse(
                "file_too_large",
                $"The replay exceeds the {quota.MaxUploadBytes / (1024 * 1024)} MB limit."));

        RenderConfig renderConfig;
        if (!string.IsNullOrWhiteSpace(config))
        {
            try
            {
                renderConfig = JsonSerializer.Deserialize<RenderConfig>(config, JsonOptions) ?? new RenderConfig();
            }
            catch (JsonException e)
            {
                return BadRequest(new ErrorResponse("invalid_config", e.Message));
            }
        }
        else
        {
            renderConfig = new RenderConfig();
        }

        if (!string.IsNullOrWhiteSpace(skin))
            renderConfig.Skin = skin;

        var configErrors = RenderConfigValidator.Validate(renderConfig);
        if (configErrors.Count > 0)
            return BadRequest(new ErrorResponse("invalid_config", string.Join(' ', configErrors)));

        if (renderConfig.Duration is double duration && duration > quota.MaxDurationSeconds)
            return BadRequest(new ErrorResponse(
                "duration_too_long",
                $"Duration exceeds the {quota.MaxDurationSeconds}s limit."));

        var now = DateTimeOffset.UtcNow;
        var quotaError = await quota.ValidateAsync(userId, now, ct);
        if (quotaError is not null)
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse("quota_exceeded", quotaError));

        // Monotonic, human-facing render number. Single-instance MAX+1 is sufficient for the MVP;
        // a dedicated sequence table would be needed for multi-host correctness.
        var displayNumber = (await db.Jobs.MaxAsync(j => (int?)j.DisplayNumber, ct) ?? 0) + 1;

        var job = new JobEntity
        {
            OwnerUserId = userId,
            // The job is not claimable until its beatmap metadata has been resolved (see below), so
            // the UI shows the real title immediately instead of the "Replay by <player>" fallback.
            Status = JobStatus.Uploaded,
            DisplayNumber = displayNumber,
            SkinName = skin,
            RenderConfigJson = JsonSerializer.Serialize(renderConfig, JsonOptions),
            Encoder = encoderResolver.Resolve(),
            Width = renderConfig.Width,
            Height = renderConfig.Height,
            Fps = renderConfig.Fps,
            Duration = renderConfig.Duration,
            MaxAttempts = quota.DefaultMaxAttempts,
            RetainedUntil = now.AddDays(quota.ResultRetentionDays),
        };

        await storage.StageReplayAsync(job.Id, file.OpenReadStream(), ct);

        var header = ReplayFileParser.TryParse(storage.StagedReplayPath(job.Id));
        if (header is null || !ReplayFileParser.IsMd5Hash(header.BeatmapMd5))
        {
            storage.DeleteStagedReplay(job.Id);
            return BadRequest(new ErrorResponse("invalid_replay", "The uploaded file is not a valid lazer .osr replay."));
        }

        job.ReplayMd5 = header.BeatmapMd5;
        job.PlayerUsername = header.PlayerUsername;

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        // Resolve beatmap metadata before the job becomes claimable. The worker cannot claim an
        // Uploaded job, so the shared render lock is free for the lookup and the title is available
        // by the time the client refreshes the list. Best-effort: a failed lookup leaves the job on
        // its "Replay by <player>" fallback and the worker re-resolves after the render. The wait is
        // bounded so a slow beatmap download can never hang the request; if it does not finish in
        // time the resolution keeps running in the background and updates the job when it completes.
        var resolve = mapMetadata.ResolveAndUpdateAsync(job.Id, header.BeatmapMd5);
        await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(25)));

        job.Status = JobStatus.Queued;
        await db.SaveChangesAsync(ct);

        return StatusCode(
            StatusCodes.Status202Accepted,
            new JobCreatedResponse(job.Id, job.DisplayNumber, job.Status.ToString().ToLowerInvariant()));
    }

    [HttpGet]
    public async Task<ActionResult<JobListResponse>> List(
        [FromQuery] string? status,
        CancellationToken ct,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        limit = Math.Clamp(limit, 1, 100);

        var query = db.Jobs.AsNoTracking().Include(j => j.Owner).Where(j => j.OwnerUserId == userId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<JobStatus>(status, ignoreCase: true, out var parsed))
            query = query.Where(j => j.Status == parsed);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var (positions, queueLength) = await GetQueuePositionsAsync(ct);

        return new JobListResponse(
            items.Select(j => JobMapper.ToDto(j, j.Owner?.Username, positions.GetValueOrDefault(j.Id), queueLength)).ToList(),
            total,
            offset);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<JobDto>> Get(string id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        var job = await db.Jobs.AsNoTracking().Include(j => j.Owner)
            .SingleOrDefaultAsync(j => j.Id == id && j.OwnerUserId == userId, ct);

        if (job is null)
            return NotFound(new ErrorResponse("not_found"));

        var (positions, queueLength) = await GetQueuePositionsAsync(ct);

        return JobMapper.ToDto(job, job.Owner?.Username, positions.GetValueOrDefault(job.Id), queueLength);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Cancel(string id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id && j.OwnerUserId == userId, ct);
        if (job is null)
            return NotFound(new ErrorResponse("not_found"));

        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        if (!job.Status.IsCancellable())
            return Conflict(new ErrorResponse("not_cancellable", $"Job is {job.Status.ToString().ToLowerInvariant()}."));

        job.Status = JobStatus.Cancelling;
        await db.SaveChangesAsync(ct);
        cancellation.RequestCancel(job.Id);

        return Accepted();
    }

    [HttpGet("{id}/result")]
    public async Task<IActionResult> DownloadResult(string id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        var job = await db.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(j => j.Id == id && j.OwnerUserId == userId, ct);

        if (job is null)
            return NotFound(new ErrorResponse("not_found"));

        if (job.Status != JobStatus.Completed ||
            string.IsNullOrWhiteSpace(job.OutputPath) ||
            !System.IO.File.Exists(job.OutputPath))
        {
            return NotFound(new ErrorResponse("result_not_available"));
        }

        return PhysicalFile(job.OutputPath, "video/mp4", "lazerrender-video.mp4", enableRangeProcessing: true);
    }

    private async Task<(Dictionary<string, int> Positions, int Length)> GetQueuePositionsAsync(CancellationToken ct)
    {
        var queuedIds = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued)
            .OrderBy(j => j.DisplayNumber)
            .Select(j => j.Id)
            .ToListAsync(ct);

        var positions = new Dictionary<string, int>(queuedIds.Count);
        for (var i = 0; i < queuedIds.Count; i++)
            positions[queuedIds[i]] = i + 1;

        return (positions, queuedIds.Count);
    }
}

using LazerRender.Api.Configuration;
using LazerRender.Api.Data;
using LazerRender.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Simple per-user abuse protection: a cap on active jobs and a cap on jobs per rolling day.
/// </summary>
public sealed class QuotaService
{
    private readonly AppDbContext db;
    private readonly QuotaOptions options;

    public QuotaService(AppDbContext db, IOptions<QuotaOptions> options)
    {
        this.db = db;
        this.options = options.Value;
    }

    public long MaxUploadBytes => options.MaxUploadBytes;
    public double MaxDurationSeconds => options.MaxDurationSeconds;
    public int DefaultMaxAttempts => options.DefaultMaxAttempts;
    public int ResultRetentionDays => options.ResultRetentionDays;
    public long MaxBeatmapBytes => options.MaxBeatmapBytes;

    public async Task<string?> ValidateAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        // Administrators are trusted and exempt from per-user quota limits. The role is read from
        // the database rather than the auth cookie so a promotion takes effect immediately (the
        // cookie's role claim is only refreshed at the next sign-in).
        var role = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Role)
            .SingleOrDefaultAsync(ct);

        if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
            return null;

        var active = await db.Jobs.CountAsync(j =>
            j.OwnerUserId == userId &&
            (j.Status == JobStatus.Queued ||
             j.Status == JobStatus.Claimed ||
             j.Status == JobStatus.Rendering ||
             j.Status == JobStatus.Finalizing), ct);

        if (active >= options.MaxActiveJobs)
            return $"You already have {active} active job(s); the limit is {options.MaxActiveJobs}.";

        var dayStart = now.Date;
        var today = await db.Jobs.CountAsync(j =>
            j.OwnerUserId == userId && j.CreatedAt >= dayStart, ct);

        if (today >= options.MaxJobsPerDay)
            return $"Daily job limit reached ({options.MaxJobsPerDay}).";

        return null;
    }
}

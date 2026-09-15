using System.Security.Claims;
using LazerRender.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Hubs;

/// <summary>
/// Realtime progress hub. Clients subscribe to a job id and the worker broadcasts progress to that
/// group.
///
/// Subscription is ownership-checked, matching the REST job endpoints: a caller-supplied <c>jobId</c>
/// is only joined when the job's <c>OwnerUserId</c> is the caller. The identifier is also validated as
/// a job id shape (32 hex characters) so a caller cannot create unbounded arbitrary groups.
/// </summary>
[Authorize]
public sealed class JobsHub : Hub
{
    private readonly AppDbContext db;
    private readonly ILogger<JobsHub> logger;

    public JobsHub(AppDbContext db, ILogger<JobsHub> logger)
    {
        this.db = db;
        this.logger = logger;
    }

    public async Task Subscribe(string jobId)
    {
        string? userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return;

        if (!await IsOwnedByAsync(db, jobId, userId))
        {
            // Do not distinguish "not found" from "not yours" in the response, but leave an audit trail.
            logger.LogWarning(
                "Rejected a progress subscription for job {JobId} by user {UserId}.",
                jobId, userId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
    }

    public async Task Unsubscribe(string jobId)
    {
        if (!IsWellFormedJobId(jobId))
            return;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Whether <paramref name="jobId"/> exists and belongs to <paramref name="userId"/>. Exposed for
    /// tests: this is the authorization decision, and it must stay in step with
    /// <c>JobsController.Get</c> / <c>DownloadResult</c>.
    /// </summary>
    internal static async Task<bool> IsOwnedByAsync(AppDbContext db, string? jobId, string userId)
    {
        if (!IsWellFormedJobId(jobId))
            return false;

        return await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == jobId && j.OwnerUserId == userId);
    }

    /// <summary>
    /// Job ids are server-generated <c>Guid.NewGuid().ToString("N")</c> values, so anything else can be
    /// rejected without a database round trip.
    /// </summary>
    private static bool IsWellFormedJobId(string? jobId) =>
        jobId is { Length: 32 } && jobId.All(Uri.IsHexDigit);
}

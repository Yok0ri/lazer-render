using LazerRender.Api.Configuration;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using LazerRender.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Controllers;

[ApiController]
[Route("/api/v1/admin")]
[Authorize(Roles = "admin")]
public sealed class AdminController : ControllerBase
{
    private readonly StorageService storage;
    private readonly AssetImportRunner importer;
    private readonly AppDbContext db;
    private readonly OsuOAuthService osu;
    private readonly RendererOptions rendererOptions;

    public AdminController(
        StorageService storage,
        AssetImportRunner importer,
        AppDbContext db,
        OsuOAuthService osu,
        IOptions<RendererOptions> rendererOptions)
    {
        this.storage = storage;
        this.importer = importer;
        this.db = db;
        this.osu = osu;
        this.rendererOptions = rendererOptions.Value;
    }

    [HttpPost("purge")]
    public async Task<IActionResult> Purge([FromQuery] string target, CancellationToken ct)
    {
        if (target is not ("beatmaps" or "skins" or "all"))
            return BadRequest(new { error = "invalid_target", detail = "target must be beatmaps, skins or all." });

        try
        {
            await importer.RunAsync(new[] { "--purge", target, "--storage", storage.RealmDirectory }, ct);
        }
        catch (AssetImportBusyException)
        {
            return Conflict(new { error = "busy", detail = "A render or import is already in progress." });
        }

        return Ok(new { purged = target });
    }

    [HttpGet("queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var queued = await db.Jobs.CountAsync(j => j.Status == JobStatus.Queued, ct);
        var active = await db.Jobs.CountAsync(j =>
            j.Status == JobStatus.Claimed ||
            j.Status == JobStatus.Rendering ||
            j.Status == JobStatus.Finalizing, ct);

        return Ok(new { queued, active });
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken ct)
    {
        var users = await db.Users
            .AsNoTracking()
            .OrderBy(u => u.OsuUserId)
            .Select(u => new AdminUserDto(
                u.Id, u.OsuUserId, u.Username, u.Role, u.IsAllowed, u.CreatedAt, u.LastLoginAt))
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost("users/allow")]
    public async Task<IActionResult> AllowUser([FromBody] AllowUserRequest request, CancellationToken ct)
    {
        long osuUserId;
        string? username = request.Username;

        if (request.OsuUserId is > 0)
        {
            osuUserId = request.OsuUserId.Value;
        }
        else if (!string.IsNullOrWhiteSpace(request.Username))
        {
            var token = string.IsNullOrWhiteSpace(rendererOptions.AvatarApiKey)
                ? Environment.GetEnvironmentVariable("OSU_API_KEY")
                : rendererOptions.AvatarApiKey;

            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(new ErrorResponse(
                    "api_key_required",
                    "Set Renderer:AvatarApiKey (or OSU_API_KEY) to resolve users by username."));

            OsuUserResponse resolved;
            try
            {
                resolved = await osu.GetPublicUserAsync(request.Username, token, ct);
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponse("lookup_failed", e.Message));
            }

            osuUserId = resolved.Id;
            username = resolved.Username;
        }
        else
        {
            return BadRequest(new ErrorResponse("identifier_required", "Provide osuUserId or username."));
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.OsuUserId == osuUserId, ct);
        if (user is null)
        {
            user = new UserEntity
            {
                OsuUserId = osuUserId,
                Username = string.IsNullOrWhiteSpace(username) ? $"osu-{osuUserId}" : username,
                Role = "user",
                IsAllowed = true,
            };
            db.Users.Add(user);
        }
        else
        {
            user.IsAllowed = true;
            if (!string.IsNullOrWhiteSpace(username))
                user.Username = username;
        }

        await db.SaveChangesAsync(ct);

        return Ok(new AdminUserDto(
            user.Id, user.OsuUserId, user.Username, user.Role, user.IsAllowed, user.CreatedAt, user.LastLoginAt));
    }

    [HttpPost("users/revoke")]
    public async Task<IActionResult> RevokeUser([FromBody] RevokeUserRequest request, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.OsuUserId == request.OsuUserId, ct);
        if (user is null)
            return NotFound(new ErrorResponse("not_found"));

        user.IsAllowed = false;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record AllowUserRequest(long? OsuUserId, string? Username);
public sealed record RevokeUserRequest(long OsuUserId);

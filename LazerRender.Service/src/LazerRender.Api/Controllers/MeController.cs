using System.Security.Claims;
using LazerRender.Api.Data;
using LazerRender.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Controllers;

[ApiController]
[Route("/api/v1")]
[Authorize]
public sealed class MeController : ControllerBase
{
    private readonly AppDbContext db;
    private readonly ILogger<MeController> logger;

    public MeController(AppDbContext db, ILogger<MeController> logger)
    {
        this.db = db;
        this.logger = logger;
    }

    [HttpGet("me")]
    public async Task<ActionResult<ApiUserDto>> Me(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            logger.LogWarning("/api/v1/me called without a valid authenticated identity.");
            return Unauthorized();
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            logger.LogWarning("/api/v1/me: no user row for id {UserId}.", userId);
            return Unauthorized();
        }

        return new ApiUserDto(user.Id, user.OsuUserId, user.Username, user.AvatarUrl, user.Role, user.CreatedAt);
    }
}

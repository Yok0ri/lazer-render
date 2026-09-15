using System.Security.Claims;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using LazerRender.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Controllers;

[ApiController]
[Route("/api/v1/presets")]
[Authorize]
public sealed class PresetsController : ControllerBase
{
    private readonly AppDbContext db;

    public PresetsController(AppDbContext db)
    {
        this.db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PresetListResponse>> List(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        var items = await db.Presets
            .AsNoTracking()
            .Where(p => p.OwnerUserId == userId)
            .OrderBy(p => p.Name)
            .Select(p => new PresetDto(p.Id, p.Name, p.ConfigJson, p.CreatedAt, p.UpdatedAt))
            .ToListAsync(ct);

        return new PresetListResponse(items);
    }

    [HttpPost]
    public async Task<ActionResult<PresetDto>> Save([FromBody] SavePresetRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.ConfigJson))
            return BadRequest(new ErrorResponse("config_required"));

        // A preset is a small settings document; without this an authenticated user could store the
        // full body-limit per preset and inflate every preset listing. See PresetGuard.
        string? configError = PresetGuard.Validate(request.ConfigJson);
        if (configError is not null)
            return BadRequest(new ErrorResponse("invalid_config", configError));

        var name = string.IsNullOrWhiteSpace(request.Name) ? "Unnamed preset" : request.Name.Trim();
        if (name.Length > 64)
            name = name[..64];

        var existing = await db.Presets.SingleOrDefaultAsync(
            p => p.OwnerUserId == userId && p.Name == name, ct);

        if (existing is not null && !request.Overwrite)
            return Conflict(new ErrorResponse("preset_exists", $"A preset named \"{name}\" already exists."));

        if (existing is null)
        {
            var count = await db.Presets.CountAsync(p => p.OwnerUserId == userId, ct);

            if (count >= PresetGuard.MaxPresetsPerUser)
            {
                return BadRequest(new ErrorResponse(
                    "preset_limit_reached",
                    $"You already have {count} presets; the limit is {PresetGuard.MaxPresetsPerUser}."));
            }
        }

        var preset = existing ?? new PresetEntity { OwnerUserId = userId, Name = name };
        preset.ConfigJson = request.ConfigJson;
        preset.UpdatedAt = DateTimeOffset.UtcNow;

        if (existing is null)
            db.Presets.Add(preset);

        await db.SaveChangesAsync(ct);

        return new PresetDto(preset.Id, preset.Name, preset.ConfigJson, preset.CreatedAt, preset.UpdatedAt);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        var preset = await db.Presets.SingleOrDefaultAsync(
            p => p.Id == id && p.OwnerUserId == userId, ct);

        if (preset is null)
            return NotFound(new ErrorResponse("not_found"));

        db.Presets.Remove(preset);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record SavePresetRequest(string? Name, string ConfigJson, bool Overwrite = false);

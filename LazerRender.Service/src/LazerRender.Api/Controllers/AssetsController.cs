using System.Security.Claims;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Controllers;

[ApiController]
[Route("/api/v1")]
[Authorize]
public sealed class AssetsController : ControllerBase
{
    private readonly StorageService storage;
    private readonly AssetImportRunner importer;
    private readonly AppDbContext db;
    private readonly QuotaService quota;
    private readonly ILogger<AssetsController> logger;

    public AssetsController(
        StorageService storage,
        AssetImportRunner importer,
        AppDbContext db,
        QuotaService quota,
        ILogger<AssetsController> logger)
    {
        this.storage = storage;
        this.importer = importer;
        this.db = db;
        this.quota = quota;
        this.logger = logger;
    }

    [HttpPost("skins")]
    public async Task<IActionResult> UploadSkin(CancellationToken ct)
    {
        var file = Request.Form.Files.GetFile("file");
        logger.LogInformation(
            "Skin upload: contentType={ContentType} files={Files} length={Length}",
            Request.ContentType,
            Request.Form.Files.Count,
            file?.Length ?? 0);

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "skin_required" });

        if (!file.FileName.EndsWith(".osk", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "invalid_extension", detail = "Only .osk skin archives are accepted." });

        if (file.Length > 200 * 1024 * 1024)
            return BadRequest(new { error = "file_too_large", detail = "Skin exceeds the 200 MB limit." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var tempPath = Path.Combine(storage.UploadsDirectory, $"{Guid.NewGuid():N}.osk");

        await using (var target = System.IO.File.Create(tempPath))
        {
            await file.OpenReadStream().CopyToAsync(target, ct);
        }

        try
        {
            // The package is expanded by the engine's third-party parsers, which run in the process
            // holding the GPU context and the render credentials. Reject implausible archives first.
            string? archiveError = ArchiveGuard.Validate(tempPath, file.Length);
            if (archiveError is not null)
                return BadRequest(new { error = "invalid_archive", detail = archiveError });

            await importer.RunAsync(new[] { "--import-skin", tempPath, "--storage", storage.RealmDirectory }, ct);
        }
        catch (AssetImportBusyException)
        {
            return Conflict(new { error = "busy", detail = "A render or import is already in progress." });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }

        db.Skins.Add(new SkinEntity
        {
            Name = Path.GetFileNameWithoutExtension(file.FileName),
            UploadedBy = userId,
        });
        await db.SaveChangesAsync(ct);

        return Ok(new { imported = true, name = Path.GetFileNameWithoutExtension(file.FileName) });
    }

    [HttpGet("skins")]
    public async Task<IActionResult> ListSkins(CancellationToken ct)
    {
        var skins = await db.Skins
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.ImportedAt })
            .ToListAsync(ct);

        return Ok(skins);
    }

    /// <summary>
    /// Deletes a skin. Skins are a shared library, so every authenticated user may see them, but only
    /// the user who uploaded one (or an admin) may delete it. The engine's Realm copy is not removed —
    /// there is no per-skin engine delete — so it stays until `--purge skins`.
    /// </summary>
    [HttpDelete("skins/{id}")]
    public async Task<IActionResult> DeleteSkin(string id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();

        var skin = await db.Skins.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (skin is null)
            return NotFound(new { error = "not_found" });

        if (!MayDelete(skin, userId, User.IsInRole("admin")))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "not_owner" });

        db.Skins.Remove(skin);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may delete <paramref name="skin"/>: the uploader, or any admin.
    /// Exposed for tests.
    /// </summary>
    internal static bool MayDelete(SkinEntity skin, string userId, bool isAdmin) =>
        isAdmin || skin.UploadedBy == userId;

    [HttpPost("beatmaps")]
    public async Task<IActionResult> UploadBeatmap(CancellationToken ct)
    {
        var file = Request.Form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "beatmap_required" });

        var name = file.FileName;
        if (!name.EndsWith(".osz", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "invalid_extension", detail = "Only .osz/.osu beatmap files are accepted." });
        }

        if (file.Length > quota.MaxBeatmapBytes)
        {
            return BadRequest(new
            {
                error = "file_too_large",
                detail = $"Beatmap package exceeds the {quota.MaxBeatmapBytes / (1024 * 1024)} MB limit.",
            });
        }

        var tempPath = Path.Combine(storage.UploadsDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(name)}");

        await using (var target = System.IO.File.Create(tempPath))
        {
            await file.OpenReadStream().CopyToAsync(target, ct);
        }

        try
        {
            // Only .osz is an archive; a bare .osu file is parsed directly by the engine.
            if (name.EndsWith(".osz", StringComparison.OrdinalIgnoreCase))
            {
                string? archiveError = ArchiveGuard.Validate(tempPath, file.Length);
                if (archiveError is not null)
                    return BadRequest(new { error = "invalid_archive", detail = archiveError });
            }

            await importer.RunAsync(new[] { "--import-map", tempPath, "--storage", storage.RealmDirectory }, ct);
        }
        catch (AssetImportBusyException)
        {
            return Conflict(new { error = "busy", detail = "A render or import is already in progress." });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }

        return Ok(new { imported = true });
    }

    [HttpGet("beatmaps/cache/{md5}")]
    public async Task<IActionResult> GetBeatmapCache(string md5, CancellationToken ct)
    {
        var row = await db.BeatmapCache.AsNoTracking().SingleOrDefaultAsync(b => b.Md5 == md5, ct);
        return Ok(new { md5, imported = row?.Imported ?? false });
    }
}

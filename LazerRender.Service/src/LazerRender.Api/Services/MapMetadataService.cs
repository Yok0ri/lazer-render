using System.Text.Json;
using LazerRender.Api.Configuration;
using LazerRender.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Resolves beatmap metadata for a replay's MD5 hash by calling the engine's <c>--map-info</c>
/// verb and caching the result. Metadata is best-effort: if the beatmap is not imported yet or a
/// render is in progress, the job simply keeps its filename fallback.
/// </summary>
public sealed class MapMetadataService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly AssetImportRunner runner;
    private readonly RenderLockService renderLock;
    private readonly StorageService storage;
    private readonly RendererOptions rendererOptions;
    private readonly ILogger<MapMetadataService> logger;

    public MapMetadataService(
        IServiceScopeFactory scopeFactory,
        AssetImportRunner runner,
        RenderLockService renderLock,
        StorageService storage,
        IOptions<RendererOptions> rendererOptions,
        ILogger<MapMetadataService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.renderLock = renderLock;
        this.storage = storage;
        this.rendererOptions = rendererOptions.Value;
        this.logger = logger;
    }

    public async Task ResolveAndUpdateAsync(string jobId, string md5)
    {
        try
        {
            // Cache hit: update the job immediately.
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cached = await db.BeatmapCache.AsNoTracking().SingleOrDefaultAsync(b => b.Md5 == md5);
                if (cached is not null && cached.Title is not null)
                {
                    Apply(db, jobId, new MapMetadataResult(cached.Title, cached.Artist ?? "", cached.Creator ?? "", cached.Version ?? "", cached.Stars ?? 0));
                    await db.SaveChangesAsync();
                    return;
                }
            }

            // A render holds the shared lock for its whole duration; do not block a running render.
            if (!await renderLock.Gate.WaitAsync(TimeSpan.FromSeconds(3)))
                return;

            try
            {
                var args = new List<string> { "--map-info", md5, "--storage", storage.RealmDirectory };

                // Download + import the beatmap when it is not in the engine database yet, so the
                // job card can show the real title before the render starts (the render would
                // otherwise download it mid-render).
                if (rendererOptions.DownloadMissing)
                    args.Add("--download-missing");

                var stdout = await runner.CaptureAsync(args, CancellationToken.None);

                var metadata = Parse(stdout);

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var row = await db.BeatmapCache.SingleOrDefaultAsync(b => b.Md5 == md5);
                if (row is null)
                {
                    row = new BeatmapCacheEntity { Md5 = md5 };
                    db.BeatmapCache.Add(row);
                }

                row.Imported = metadata is not null;
                if (metadata is not null)
                {
                    row.Title = metadata.Title;
                    row.Artist = metadata.Artist;
                    row.Creator = metadata.Creator;
                    row.Version = metadata.Version;
                    row.Stars = metadata.Stars;

                    Apply(db, jobId, metadata);
                }

                await db.SaveChangesAsync();
            }
            finally
            {
                renderLock.Gate.Release();
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Map metadata resolution failed for job {JobId}.", jobId);
        }
    }

    private static void Apply(AppDbContext db, string jobId, MapMetadataResult metadata)
    {
        var job = db.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null)
            return;

        job.MapTitle = metadata.Title;
        job.MapArtist = metadata.Artist;
        job.MapCreator = metadata.Creator;
        job.MapVersion = metadata.Version;
        job.MapStars = metadata.Stars;
    }

    private static MapMetadataResult? Parse(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("found", out var found) || !found.GetBoolean())
                    return null;

                if (!root.TryGetProperty("title", out var title))
                    return null;

                return new MapMetadataResult(
                    title.GetString() ?? "",
                    GetString(root, "artist"),
                    GetString(root, "creator"),
                    GetString(root, "version"),
                    GetDouble(root, "stars"));
            }
            catch (JsonException)
            {
                // Ignore non-JSON / malformed lines.
            }
        }

        return null;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static double GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    public sealed record MapMetadataResult(string Title, string Artist, string Creator, string Version, double Stars);
}

using LazerRender.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Resolves and manages the service filesystem layout. All directories are created eagerly so
/// downstream code can rely on them existing.
/// </summary>
public sealed class StorageService
{
    private readonly StorageOptions options;
    private readonly IHostEnvironment environment;

    public StorageService(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        this.options = options.Value;
        this.environment = environment;
        EnsureDirectories();
    }

    public string DataDirectory => Resolve(options.DataDirectory, "data");
    public string UploadsDirectory => Resolve(options.UploadsDirectory, Path.Combine(DataDirectory, "uploads"));
    public string JobsDirectory => Resolve(options.JobsDirectory, Path.Combine(DataDirectory, "jobs"));
    public string ResultsDirectory => Resolve(options.ResultsDirectory, Path.Combine(DataDirectory, "results"));
    public string RealmDirectory => Resolve(options.RealmDirectory, Path.Combine(DataDirectory, "realm"));

    public string StagedReplayPath(string jobId) => Path.Combine(UploadsDirectory, $"{jobId}.osr");
    public string JobDirectory(string jobId) => Path.Combine(JobsDirectory, jobId);
    public string RenderConfigPath(string jobId) => Path.Combine(JobDirectory(jobId), "render-config.json");
    public string OutputDirectory(string jobId) => Path.Combine(JobDirectory(jobId), "output");
    public string ResultPath(string jobId) => Path.Combine(ResultsDirectory, jobId, "output.mp4");

    public async Task StageReplayAsync(string jobId, Stream source, CancellationToken ct)
    {
        var path = StagedReplayPath(jobId);
        Directory.CreateDirectory(UploadsDirectory);

        await using var target = File.Create(path);
        await source.CopyToAsync(target, ct);
    }

    public async Task WriteRenderConfigAsync(string jobId, string json, CancellationToken ct)
    {
        var jobDirectory = JobDirectory(jobId);
        Directory.CreateDirectory(jobDirectory);
        await File.WriteAllTextAsync(RenderConfigPath(jobId), json, ct);
    }

    public void DeleteStagedReplay(string jobId)
    {
        var path = StagedReplayPath(jobId);
        if (File.Exists(path))
            File.Delete(path);
    }

    public void DeleteJobDirectory(string jobId)
    {
        var path = JobDirectory(jobId);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private string Resolve(string configured, string fallback)
    {
        var basePath = string.IsNullOrWhiteSpace(configured) ? fallback : configured;

        var full = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(environment.ContentRootPath, basePath);

        return Path.GetFullPath(full);
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(UploadsDirectory);
        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(ResultsDirectory);
        Directory.CreateDirectory(RealmDirectory);
    }
}

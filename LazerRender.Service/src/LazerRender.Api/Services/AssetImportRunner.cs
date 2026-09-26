using System.Diagnostics;
using LazerRender.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Thrown when an asset import is requested while a render or another import is running.
/// </summary>
public sealed class AssetImportBusyException : Exception
{
    public AssetImportBusyException() : base("A render or import is already in progress.") { }
}

/// <summary>
/// Runs one-shot LazerRender asset operations (<c>--import-skin</c>, <c>--import-map</c>,
/// <c>--purge</c>, <c>--map-info</c>) through the headless runner.
/// </summary>
public sealed class AssetImportRunner
{
    /// <summary>
    /// Import stdout/stderr derives from the uploaded package, so only an excerpt is logged rather than
    /// the whole stream.
    /// </summary>
    private const int MaxExcerptLength = 1000;

    private readonly RenderLockService renderLock;
    private readonly RendererOptions options;
    private readonly IHostEnvironment environment;
    private readonly ILogger<AssetImportRunner> logger;

    public AssetImportRunner(
        RenderLockService renderLock,
        IOptions<RendererOptions> options,
        IHostEnvironment environment,
        ILogger<AssetImportRunner> logger)
    {
        this.renderLock = renderLock;
        this.options = options.Value;
        this.environment = environment;
        this.logger = logger;
    }

    /// <summary>Runs an asset operation behind the shared render lock. Returns the engine's stdout.</summary>
    public async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!await renderLock.Gate.WaitAsync(0, ct))
            throw new AssetImportBusyException();

        try
        {
            var (exitCode, stdout, stderr) = await RunProcessAsync(args, ct);

            logger.LogInformation(
                "Asset operation exited with code {ExitCode}. stdout: {Stdout} stderr: {Stderr}",
                exitCode, Excerpt(stdout), Excerpt(stderr));

            if (exitCode != 0)
                throw new InvalidOperationException($"LazerRender exited with code {exitCode}: {stderr}");

            return stdout;
        }
        finally
        {
            renderLock.Gate.Release();
        }
    }

    /// <summary>
    /// Runs a read-only LazerRender operation and returns its stdout. The caller is responsible
    /// for acquiring the shared render lock.
    /// </summary>
    public async Task<string> CaptureAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var (_, stdout, _) = await RunProcessAsync(args, ct);
        return stdout;
    }

    private static string Excerpt(string text) =>
        text.Length <= MaxExcerptLength
            ? text
            : string.Concat(text.AsSpan(0, MaxExcerptLength), "…[truncated]");

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveSetsid(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(ResolveRunnerScript());
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private string ResolveRunnerScript()
    {
        if (!string.IsNullOrWhiteSpace(options.RunnerScript))
        {
            var configured = Path.IsPathRooted(options.RunnerScript)
                ? options.RunnerScript
                : Path.Combine(environment.ContentRootPath, options.RunnerScript);

            if (File.Exists(configured))
                return Path.GetFullPath(configured);
        }

        // See RendererProcessRunner: the search is a development convenience and is confined to the
        // content root outside Development, so a writable ancestor cannot substitute the script.
        bool confinedToContentRoot = !environment.IsDevelopment();
        var directory = new DirectoryInfo(environment.ContentRootPath);

        while (directory is not null)
        {
            // The engine's runner lives in LazerRender.Game/scripts/. The second candidate keeps
            // backwards compatibility with a top-level scripts/ layout.
            var candidates = new[]
            {
                Path.Combine(directory.FullName, "LazerRender.Game", "scripts", "run-headless.sh"),
                Path.Combine(directory.FullName, "scripts", "run-headless.sh"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            if (confinedToContentRoot)
                break;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate LazerRender.Game/scripts/run-headless.sh inside the content root. "
            + "Set Renderer:RunnerScript to an absolute path.");
    }

    private string ResolveSetsid()
    {
        string[] candidates = { "/usr/bin/setsid", "/bin/setsid", "/usr/local/bin/setsid" };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        if (!environment.IsDevelopment())
            throw new InvalidOperationException("setsid was not found at any standard absolute path.");

        return "setsid";
    }
}

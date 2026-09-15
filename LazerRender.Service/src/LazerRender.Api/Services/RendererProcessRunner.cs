using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LazerRender.Api.Configuration;
using LazerRender.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

public sealed record RenderInvocation(
    string JobId,
    string ReplayPath,
    string RenderConfigPath,
    string OutputDirectory,
    string StorageDirectory,
    string Encoder,
    bool DownloadMissing,
    // Path to the owner-only secrets document handed to the engine (--secrets-file), or null when the
    // render needs no osu! credentials. The credential itself is never an argument, so it cannot be
    // read from the process command line.
    string? SecretsFilePath,
    // The credential values, held in-process only, so the log bridge can redact an accidental echo.
    IReadOnlyList<string> RedactedValues);

public sealed record RenderRunResult(bool Success, int ExitCode, bool Cancelled);

/// <summary>
/// Spawns LazerRender via the headless runner script and parses its stdout JSON progress lines.
/// The child runs under <c>setsid</c> so cancellation can SIGTERM the whole process group
/// (script + LazerRender + FFmpeg) cleanly.
///
/// The engine's non-JSON output (its <c>[runtime]</c> log) is forwarded to this service's logger:
/// notable lines at Information/Warning so an operator can actually see what a render did, and the
/// rest at Debug so a render's hundreds of framework lines stay out of the default log.
/// </summary>
public sealed class RendererProcessRunner
{
    /// <summary>
    /// Engine log lines worth surfacing by default. Everything else is Debug.
    /// </summary>
    private static readonly string[] notableEngineMarkers =
    {
        "Leaderboard:", "osu! API login:", "Avatar:", "Replay complete;", "Recorded ",
        "treating it as ranked", "HUD visibility", "HudVisibilityFilter", "failure",
    };

    /// <summary>Engine lines that indicate something went wrong, surfaced as warnings.</summary>
    private static readonly string[] problemEngineMarkers =
    {
        "error", "exception", "failed", "failure", "unhandled", "fatal", "crash", "denied",
    };

    /// <summary>Catches a credential in a shape we were not handed explicitly.</summary>
    private static readonly Regex BearerPattern = new(
        @"\bBearer\s+[^\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Upper bound on a single forwarded engine log line.</summary>
    private const int MaxLoggedLineLength = 2000;

    /// <summary>
    /// Upper bound on how much engine output one render may push into the service log. Engine output is
    /// derived from user-supplied inputs (replay usernames, beatmap metadata, uploaded archives), so a
    /// broken or adversarial upload could otherwise emit unbounded text into the log.
    /// </summary>
    private const long MaxLoggedBytesPerRun = 256 * 1024;

    private const int SigTerm = 15;
    private const int SigKill = 9;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RendererOptions options;
    private readonly IHostEnvironment environment;
    private readonly ILogger<RendererProcessRunner> logger;

    public RendererProcessRunner(
        IOptions<RendererOptions> options,
        IHostEnvironment environment,
        ILogger<RendererProcessRunner> logger)
    {
        this.options = options.Value;
        this.environment = environment;
        this.logger = logger;
    }

    public async Task<RenderRunResult> RunAsync(
        RenderInvocation invocation,
        CancellationToken ct,
        Func<RenderProgress, Task>? onProgress)
    {
        string script = ResolveRunnerScript();

        var psi = new ProcessStartInfo
        {
            FileName = ResolveSetsid(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(script);
        foreach (var arg in BuildArgs(invocation))
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var doneSeen = false;
        var logBudget = new LogBudget(MaxLoggedBytesPerRun);

        var progressTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!TryParseProgress(line, out var progress))
                {
                    logEngineLine("stdout", line, invocation.RedactedValues, logBudget);
                    continue;
                }

                if (onProgress is not null)
                    await onProgress(progress);

                if (progress.IsDone)
                    doneSeen = true;
            }
        }, CancellationToken.None);

        // Drain stderr so the child never blocks on a full pipe, forwarding it to the service log
        // instead of discarding it.
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                logEngineLine("stderr", line, invocation.RedactedValues, logBudget);
        }, CancellationToken.None);

        bool cancelled = false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.ProcessTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            logger.LogWarning("Render {JobId} cancelled or timed out; sending SIGTERM to process group.", invocation.JobId);

            SendSignal(process.Id, SigTerm, processGroup: true);

            using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(killCts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Render {JobId} did not exit after SIGTERM; sending SIGKILL.", invocation.JobId);
                SendSignal(process.Id, SigKill, processGroup: true);
                await process.WaitForExitAsync();
            }
        }

        await progressTask;

        // Both streams must be drained to completion before returning, otherwise the tail of the
        // engine's log (usually the interesting part) can be lost when the process exits.
        await stderrTask;

        return new RenderRunResult(doneSeen, process.ExitCode, cancelled);
    }

    /// <summary>
    /// Forwards one line of the engine's own log to the service log. Notable lines are visible at the
    /// default level; the engine's framework chatter is kept at Debug so it does not drown the log.
    /// Credential values are redacted first, the line is length-capped, and the whole render shares a
    /// byte budget so a misbehaving engine cannot fill the log.
    /// </summary>
    private void logEngineLine(string source, string line, IReadOnlyList<string> secrets, LogBudget budget)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        line = Redact(line, secrets);

        if (line.Length > MaxLoggedLineLength)
            line = string.Concat(line.AsSpan(0, MaxLoggedLineLength), "…[truncated]");

        if (!budget.TryReserve(line.Length))
        {
            if (budget.NotifyExhaustedOnce())
            {
                logger.LogWarning(
                    "[engine/{Source}] further engine output suppressed for this render (budget {Budget} bytes reached).",
                    source, MaxLoggedBytesPerRun);
            }

            return;
        }

        if (problemEngineMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
            logger.LogWarning("[engine/{Source}] {Line}", source, line);
        else if (notableEngineMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
            logger.LogInformation("[engine/{Source}] {Line}", source, line);
        else
            logger.LogDebug("[engine/{Source}] {Line}", source, line);
    }

    /// <summary>
    /// Replaces every known credential value with a marker, and masks anything that looks like a bearer
    /// token, so a credential can never reach the log.
    /// </summary>
    internal static string Redact(string line, IReadOnlyList<string> secrets)
    {
        foreach (string secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                line = line.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return BearerPattern.Replace(line, "Bearer [redacted]");
    }

    /// <summary>Per-render byte budget for forwarded engine output.</summary>
    private sealed class LogBudget(long limit)
    {
        private long remaining = limit;
        private int notified;

        /// <summary>Reserves room for a line; false once the budget is exhausted.</summary>
        public bool TryReserve(int bytes) => Interlocked.Add(ref remaining, -bytes) > 0;

        /// <summary>True exactly once, so the "output suppressed" notice is logged a single time.</summary>
        public bool NotifyExhaustedOnce() => Interlocked.Exchange(ref notified, 1) == 0;
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

        // The auto-detected search is a development convenience. Outside Development it is confined to
        // the content root: walking into a parent directory would let a writable ancestor substitute the
        // script that gets executed. A deployed bundle that keeps the engine tree inside the content root
        // still resolves without configuration.
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

    internal static IEnumerable<string> BuildArgs(RenderInvocation invocation)
    {
        yield return "--replay";
        yield return invocation.ReplayPath;
        yield return "--output";
        yield return invocation.OutputDirectory;
        yield return "--storage";
        yield return invocation.StorageDirectory;
        yield return "--render-config";
        yield return invocation.RenderConfigPath;
        yield return "--encoder";
        yield return invocation.Encoder;

        if (invocation.DownloadMissing)
            yield return "--download-missing";

        // Credentials travel in a supervisor-written, owner-only file rather than as arguments, so a
        // live osu! token is not visible in `ps` / `/proc/<pid>/cmdline` for the render's duration.
        if (!string.IsNullOrWhiteSpace(invocation.SecretsFilePath))
        {
            yield return "--secrets-file";
            yield return invocation.SecretsFilePath;
        }
    }

    private static bool TryParseProgress(string line, out RenderProgress progress)
    {
        progress = null!;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{'))
            return false;

        try
        {
            var candidate = JsonSerializer.Deserialize<RenderProgress>(trimmed, JsonOptions);
            if (candidate is null || candidate.Type != "progress")
                return false;

            progress = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string ResolveSetsid()
    {
        string[] candidates = { "/usr/bin/setsid", "/bin/setsid", "/usr/local/bin/setsid" };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Falling back to a PATH lookup means a poisoned PATH could substitute the wrapper that is
        // trusted to create the process group.
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("setsid was not found at any standard absolute path.");

        return "setsid";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static void SendSignal(int pid, int sig, bool processGroup)
    {
        _ = kill(processGroup ? -pid : pid, sig);
    }
}

using LazerRender.Api.Configuration;
using LazerRender.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// The engine's own log used to be thrown away — non-JSON stdout lines were skipped and stderr was
/// drained into nothing — which made render problems (a rejected API token, a failed leaderboard
/// fetch) invisible from the service. These tests pin the forwarding behaviour: notable lines at
/// Information, problem lines at Warning, framework chatter at Debug.
/// </summary>
public sealed class RendererProcessRunnerLogTests
{
    [Fact]
    public async Task Engine_diagnostics_reach_the_service_log()
    {
        if (OperatingSystem.IsWindows())
            return; // the runner is Linux-only (setsid + process-group signals).

        string dir = Directory.CreateTempSubdirectory("lazerrender-runner-").FullName;

        try
        {
            string script = WriteScript(dir, """
                #!/usr/bin/env bash
                echo '{"type":"progress","phase":"RENDERING_FRAMES","frame":60,"total":450,"fps":123.4}'
                echo '[runtime] 2026-01-01 00:00:00 [verbose]: Leaderboard: fetched 50 global score(s) of 1234 for the scoreboard.'
                echo '[runtime] 2026-01-01 00:00:00 [verbose]: some framework chatter' >&2
                echo '[runtime] 2026-01-01 00:00:00 [verbose]: osu! API login: failed to inject the user token: nope' >&2
                echo '{"type":"progress","phase":"DONE","frame":450,"total":450,"fps":0}'
                """);

            var log = new RecordingLogger();
            RenderRunResult result = await RunAsync(script, dir, log, onProgress: null);

            Assert.True(result.Success);

            Assert.True(
                log.Entries.Count == 3,
                $"expected 3 log entries, got {log.Entries.Count}: {log.Describe()}");

            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Information && e.Message.Contains("Leaderboard: fetched 50 global"));

            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("failed to inject the user token"));

            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Debug && e.Message.Contains("framework chatter"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Progress_lines_are_not_forwarded_as_log_noise()
    {
        if (OperatingSystem.IsWindows())
            return;

        string dir = Directory.CreateTempSubdirectory("lazerrender-runner-").FullName;

        try
        {
            string script = WriteScript(dir, """
                #!/usr/bin/env bash
                echo '{"type":"progress","phase":"RENDERING_FRAMES","frame":60,"total":450,"fps":123.4}'
                echo '{"type":"progress","phase":"DONE","frame":450,"total":450,"fps":0}'
                """);

            var log = new RecordingLogger();
            var phases = new List<string>();

            await RunAsync(script, dir, log, progress =>
            {
                phases.Add(progress.Phase);
                return Task.CompletedTask;
            });

            Assert.Equal(new[] { "RENDERING_FRAMES", "DONE" }, phases);
            Assert.Empty(log.Entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string WriteScript(string dir, string contents)
    {
        string script = Path.Combine(dir, "fake-engine.sh");
        File.WriteAllText(script, contents);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return script;
    }

    private static Task<RenderRunResult> RunAsync(
        string script,
        string dir,
        ILogger<RendererProcessRunner> log,
        Func<LazerRender.Contracts.RenderProgress, Task>? onProgress)
    {
        var runner = new RendererProcessRunner(
            Options.Create(new RendererOptions { RunnerScript = script }),
            new FakeHostEnvironment(dir),
            log);

        var invocation = new RenderInvocation(
            "job1", "/tmp/replay.osr", "/tmp/config.json", "/tmp/out", "/tmp/storage",
            "cpu", DownloadMissing: false, AvatarApiKey: null, OsuUserToken: null, OsuUserTokenExpiresIn: 3600);

        return runner.RunAsync(invocation, CancellationToken.None, onProgress);
    }

    /// <summary>
    /// Records log entries. Must be thread-safe: the runner drains stdout and stderr on separate
    /// tasks, so a plain <see cref="List{T}"/> silently loses entries.
    /// </summary>
    private sealed class RecordingLogger : ILogger<RendererProcessRunner>
    {
        private readonly object gate = new();
        private readonly List<(LogLevel Level, string Message)> entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (gate)
                    return entries.ToList();
            }
        }

        public string Describe()
        {
            lock (gate)
                return string.Join(" || ", entries.Select(e => $"{e.Level}:{e.Message}"));
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);

            lock (gate)
                entries.Add((logLevel, message));
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string contentRootPath) => ContentRootPath = contentRootPath;

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "LazerRender.Worker.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

using LazerRender.Api.Configuration;
using LazerRender.Api.Services;
using LazerRender.Api.Services.Logging;
using LazerRender.Contracts;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// Phase 8.2: the log record model, the bounded ring buffers, the <c>ILoggerProvider</c> that feeds the
/// service stream, and the engine forwarder that feeds the engine stream. These tests pin the
/// contracts the Phase 8.3 admin console will consume.
/// </summary>
public sealed class LoggingPipelineTests
{
    [Fact]
    public void Ring_buffer_keeps_the_newest_records_and_counts_drops()
    {
        var buffer = new RingLogBuffer(capacity: 3);

        for (int i = 1; i <= 5; i++)
            buffer.Write(LogSource.Service, LogSeverity.Information, $"line {i}");

        var snapshot = buffer.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(new[] { "line 3", "line 4", "line 5" }, snapshot.Select(r => r.Message));
        Assert.Equal(new long[] { 3, 4, 5 }, snapshot.Select(r => r.Sequence));
        Assert.Equal(2, buffer.DroppedCount);
    }

    [Fact]
    public void Snapshot_after_sequence_returns_only_newer_records()
    {
        var buffer = new RingLogBuffer(10);
        buffer.Write(LogSource.Service, LogSeverity.Information, "a");
        buffer.Write(LogSource.Service, LogSeverity.Information, "b");

        IReadOnlyList<LogRecord> first = buffer.Snapshot();
        long lastSeen = first[^1].Sequence;

        buffer.Write(LogSource.Engine, LogSeverity.Warning, "c");

        var delta = buffer.Snapshot(lastSeen);

        Assert.Single(delta);
        Assert.Equal("c", delta[0].Message);
        Assert.Equal(LogSource.Engine, delta[0].Source);
    }

    [Fact]
    public void Concurrent_writes_keep_the_buffer_consistent()
    {
        var buffer = new RingLogBuffer(100);

        Parallel.For(0, 2000, i => buffer.Write(LogSource.Service, LogSeverity.Information, $"line {i}"));

        var snapshot = buffer.Snapshot();

        Assert.Equal(100, snapshot.Count);
        Assert.Equal(100, snapshot.Select(r => r.Sequence).Distinct().Count());

        for (int i = 1; i < snapshot.Count; i++)
            Assert.True(snapshot[i].Sequence > snapshot[i - 1].Sequence);
    }

    [Fact]
    public void Subscribe_tracks_subscribers_and_delivers_records()
    {
        var buffer = new RingLogBuffer(10);
        var received = new List<LogRecord>();

        using (IDisposable subscription = buffer.Subscribe(received.Add))
        {
            Assert.Equal(1, buffer.SubscriberCount);
            buffer.Write(LogSource.Service, LogSeverity.Information, "hello");
        }

        Assert.Equal(0, buffer.SubscriberCount);
        Assert.Single(received);
        Assert.Equal("hello", received[0].Message);

        // Once disposed, no further records are delivered.
        buffer.Write(LogSource.Service, LogSeverity.Information, "after");
        Assert.Single(received);
    }

    [Fact]
    public void Minimum_severity_filters_lower_records()
    {
        var buffer = new RingLogBuffer(10, LogSeverity.Information);

        buffer.Write(LogSource.Service, LogSeverity.Debug, "debug");
        buffer.Write(LogSource.Service, LogSeverity.Warning, "warn");

        var snapshot = buffer.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal("warn", snapshot[0].Message);
    }

    [Fact]
    public void Service_logger_provider_captures_classifies_and_redacts()
    {
        var options = Options.Create(new ObservabilityOptions
        {
            ServiceBufferSize = 10,
            ServiceMinimumLevel = "Debug",
        });

        var buffer = new ServiceLogRingBuffer(options);
        var provider = new RingBufferLoggerProvider(buffer, new LogRedactor(new[] { "TOKEN-VALUE" }));
        ILogger logger = provider.CreateLogger("LazerRender.Test");

        logger.LogWarning("login used TOKEN-VALUE");

        var snapshot = buffer.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal(LogSource.Service, snapshot[0].Source);
        Assert.Equal(LogSeverity.Warning, snapshot[0].Severity);
        Assert.Contains("[LazerRender.Test]", snapshot[0].Message);
        Assert.Contains("[redacted]", snapshot[0].Message);
        Assert.DoesNotContain("TOKEN-VALUE", snapshot[0].Message);
    }

    [Fact]
    public void Engine_forwarder_classifies_redacts_and_buffers()
    {
        var buffer = new RingLogBuffer(50, LogSeverity.Debug);
        var log = new CapturingLogger();
        var forwarder = new EngineLogForwarder(log, new LogRedactor(Array.Empty<string>()), buffer);
        var budget = new LogBudget(EngineLogForwarder.MaxLoggedBytesPerRun);
        var secrets = new[] { "SECRET-TOKEN" };

        forwarder.Forward("stdout", "Leaderboard: fetched 50 global score(s)", secrets, budget);
        forwarder.Forward("stderr", "osu! API login: failed to inject SECRET-TOKEN", secrets, budget);
        forwarder.Forward("stdout", "some framework chatter", secrets, budget);

        // The existing three-level classification is preserved on the ILogger side.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Leaderboard: fetched 50"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("failed to inject"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("framework chatter"));

        // And the same records land in the engine sink, classified, with the credential gone.
        var snapshot = buffer.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, r => Assert.Equal(LogSource.Engine, r.Source));
        Assert.Contains(snapshot, r => r.Severity == LogSeverity.Information);
        Assert.Contains(snapshot, r => r.Severity == LogSeverity.Warning);
        Assert.Contains(snapshot, r => r.Severity == LogSeverity.Debug);
        Assert.DoesNotContain(snapshot, r => r.Message.Contains("SECRET-TOKEN"));
    }

    [Fact]
    public async Task Render_runner_feeds_the_engine_ring_buffer_with_redacted_lines()
    {
        if (OperatingSystem.IsWindows())
            return; // the runner is Linux-only (setsid + process-group signals).

        string dir = Directory.CreateTempSubdirectory("lazerrender-logbuf-").FullName;

        try
        {
            string script = Path.Combine(dir, "fake-engine.sh");
            File.WriteAllText(script, """
                #!/usr/bin/env bash
                echo '[runtime] Leaderboard: fetched 1 global score'
                echo '[runtime] osu! API login: injected Bearer SECRET-TOKEN-VALUE' >&2
                echo '{"type":"progress","phase":"DONE","frame":1,"total":1,"fps":0}'
                """);

            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var buffer = new EngineLogRingBuffer(Options.Create(new ObservabilityOptions
            {
                EngineBufferSize = 50,
                EngineMinimumLevel = "Debug",
            }));

            var runner = new RendererProcessRunner(
                Options.Create(new RendererOptions { RunnerScript = script }),
                new StubHostEnvironment(dir),
                new CapturingLogger(),
                new LogRedactor(Array.Empty<string>()),
                buffer);

            var invocation = new RenderInvocation(
                "job1", "/tmp/replay.osr", "/tmp/config.json", "/tmp/out", "/tmp/storage",
                "cpu", DownloadMissing: false, SecretsFilePath: null, RedactedValues: Array.Empty<string>());

            await runner.RunAsync(invocation, CancellationToken.None, onProgress: null);

            var snapshot = buffer.Snapshot();

            Assert.Contains(snapshot, r => r.Message.Contains("Leaderboard: fetched 1 global"));
            Assert.DoesNotContain(snapshot, r => r.Message.Contains("SECRET-TOKEN-VALUE"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Records log entries; thread-safe because the runner drains stdout and stderr in parallel.</summary>
    private sealed class CapturingLogger : ILogger<RendererProcessRunner>
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

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "LazerRender.Worker.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
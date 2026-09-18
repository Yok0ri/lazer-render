using System.Reflection;
using LazerRender.Api.Configuration;
using LazerRender.Api.Controllers;
using LazerRender.Api.Services;
using LazerRender.Api.Services.Logging;
using LazerRender.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// Phase 8.3: the admin observability panel's server-side contracts. The most important test here is
/// the authorization guard — every admin route must be admin-only <em>server-side</em>, so a new
/// endpoint added without the role requirement fails the suite rather than leaking data.
/// </summary>
public sealed class AdminObservabilityTests
{
    /* ---------- authorization ---------- */

    [Fact]
    public void Every_admin_controller_action_requires_the_admin_role()
    {
        Type controller = typeof(AdminController);
        List<AuthorizeAttribute> classAuthorize = controller.GetCustomAttributes<AuthorizeAttribute>(true).ToList();

        List<MethodInfo> actions = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(true).Any())
            .ToList();

        Assert.NotEmpty(actions);

        foreach (MethodInfo action in actions)
        {
            Assert.False(
                action.GetCustomAttributes<AllowAnonymousAttribute>(true).Any(),
                $"{action.Name} must not be anonymous.");

            bool admin = IsAdmin(classAuthorize)
                         || IsAdmin(action.GetCustomAttributes<AuthorizeAttribute>(true).ToList());

            Assert.True(admin, $"{action.Name} is not protected by an admin-role requirement.");
        }
    }

    [Theory]
    [InlineData("RenderPc")]
    [InlineData("Logs")]
    [InlineData("CloseLogs")]
    [InlineData("Purge")]
    [InlineData("Users")]
    public void Expected_admin_endpoints_exist(string actionName)
        => Assert.NotNull(typeof(AdminController).GetMethod(actionName));

    private static bool IsAdmin(IEnumerable<AuthorizeAttribute> attributes) =>
        attributes.Any(a => !string.IsNullOrEmpty(a.Roles)
                            && a.Roles.Split(',').Select(r => r.Trim()).Contains("admin"));

    /* ---------- log stream lifecycle ---------- */

    [Fact]
    public void Poll_returns_records_and_a_delta_then_release_clears_the_stream()
    {
        var service = new ServiceLogRingBuffer(Options.Create(new ObservabilityOptions { ServiceBufferSize = 10 }));
        var engine = new EngineLogRingBuffer(Options.Create(new ObservabilityOptions { EngineBufferSize = 10 }));
        var stream = new LogStreamService(service, engine);

        service.Write(LogSource.Service, LogSeverity.Information, "one");
        service.Write(LogSource.Service, LogSeverity.Warning, "two");

        LogSnapshotDto first = stream.Poll(LogSource.Service, 0);

        Assert.Equal(2, first.Records.Count);
        Assert.Equal("one", first.Records[0].Message);
        Assert.Equal(LogSource.Service, first.Records[0].Source);
        Assert.Equal(first.MinimumSeverity, service.MinimumSeverity.ToString().ToLowerInvariant());

        long cursor = first.Records[^1].Sequence;

        service.Write(LogSource.Service, LogSeverity.Error, "three");

        LogSnapshotDto delta = stream.Poll(LogSource.Service, cursor);
        Assert.Single(delta.Records);
        Assert.Equal("three", delta.Records[0].Message);

        stream.Release(LogSource.Service);
        Assert.Equal(0, service.Count);

        LogSnapshotDto afterRelease = stream.Poll(LogSource.Service, 0);
        Assert.Empty(afterRelease.Records);
    }

    [Fact]
    public void Poll_reports_a_stale_cursor_as_cleared()
    {
        // Capacity 3: writing 5 records evicts the first two, so a cursor of 1 is older than the
        // oldest retained record (3) and the client must reset.
        var service = new ServiceLogRingBuffer(Options.Create(new ObservabilityOptions { ServiceBufferSize = 3 }));
        var engine = new EngineLogRingBuffer(Options.Create(new ObservabilityOptions { EngineBufferSize = 10 }));
        var stream = new LogStreamService(service, engine);

        for (int i = 1; i <= 5; i++)
            service.Write(LogSource.Service, LogSeverity.Information, $"line {i}");

        LogSnapshotDto stale = stream.Poll(LogSource.Service, afterSequence: 1);

        Assert.True(stale.Cleared);
        Assert.Equal(3, stale.Records.Count);
        Assert.Equal(2, stale.Dropped);

        LogSnapshotDto fresh = stream.Poll(LogSource.Service, afterSequence: 0);
        Assert.False(fresh.Cleared);
    }

    [Fact]
    public void Idle_streams_are_cleared_by_the_sweeper()
    {
        var service = new ServiceLogRingBuffer(Options.Create(new ObservabilityOptions { ServiceBufferSize = 10 }));
        var engine = new EngineLogRingBuffer(Options.Create(new ObservabilityOptions { EngineBufferSize = 10 }));
        var stream = new LogStreamService(service, engine);

        service.Write(LogSource.Service, LogSeverity.Information, "watched");
        stream.Poll(LogSource.Service, 0);

        // Not yet idle.
        stream.ClearIdle(TimeSpan.FromMinutes(1));
        Assert.Equal(1, service.Count);

        // Idle: everything is emptied.
        stream.ClearIdle(TimeSpan.Zero);
        Assert.Equal(0, service.Count);
        Assert.Equal(0, engine.Count);
    }

    [Theory]
    [InlineData("service", true)]
    [InlineData("ENGINE", true)]
    [InlineData("bogus", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Source_parsing_accepts_only_service_and_engine(string? value, bool expected)
    {
        bool accepted = LogStreamService.TryParseSource(value, out LogSource parsed);

        Assert.Equal(expected, accepted);

        if (expected)
            Assert.True(parsed is LogSource.Service or LogSource.Engine);
    }

    /* ---------- render pc summary ---------- */

    [Fact]
    public async Task Render_pc_summary_reports_identity_and_encoder_without_probing_the_gpu()
    {
        string dir = Directory.CreateTempSubdirectory("lazerrender-renderpc-").FullName;

        try
        {
            var environment = new StubHostEnvironment(dir);
            var storage = new StorageService(Options.Create(new StorageOptions()), environment);
            var encoder = new EncoderResolver(
                Options.Create(new RendererOptions { Encoder = "cpu" }),
                NullLogger<EncoderResolver>.Instance);
            var systemInfo = new SystemInfoService(encoder, storage, NullLogger<SystemInfoService>.Instance);

            RenderPcDto pc = await systemInfo.GetAsync(refresh: false, CancellationToken.None);

            Assert.Equal("cpu", pc.Encoder);
            Assert.False(pc.EncoderAutoDetected);
            Assert.False(string.IsNullOrWhiteSpace(pc.OperatingSystem));
            Assert.False(string.IsNullOrWhiteSpace(pc.DotnetRuntime));
            Assert.False(string.IsNullOrWhiteSpace(pc.CpuModel));
            Assert.True(pc.CpuCores >= 1);
            Assert.Equal(storage.ResultsDirectory, pc.ResultsPath);

            // The cached instance is served on a non-refresh call.
            Assert.Same(pc, await systemInfo.GetAsync(refresh: false, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
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

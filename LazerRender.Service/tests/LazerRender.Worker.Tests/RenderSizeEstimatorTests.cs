using LazerRender.Api.Configuration;
using LazerRender.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LazerRender.Worker.Tests;

public sealed class RenderSizeEstimatorTests
{
    private static RenderSizeEstimator CreateEstimator(QuotaOptions? options = null)
        => new(Options.Create(options ?? new QuotaOptions()));

    [Fact]
    public void Default_limit_is_one_hour_of_1080p60()
    {
        var estimator = CreateEstimator();
        Assert.Equal(estimator.EstimateBytes(1920, 1080, 60, 3600), estimator.LimitBytes);
    }

    [Fact]
    public void Oversized_4k120_half_hour_exceeds_limit()
    {
        var estimator = CreateEstimator();
        long estimate = estimator.EstimateBytes(3840, 2160, 120, 1800);
        Assert.True(estimate > estimator.LimitBytes);
    }

    [Fact]
    public void Short_1080p60_render_is_within_limit()
    {
        var estimator = CreateEstimator();
        long estimate = estimator.EstimateBytes(1920, 1080, 60, 40);
        Assert.True(estimate < estimator.LimitBytes);
    }

    [Fact]
    public void Explicit_limit_overrides_reference()
    {
        var estimator = CreateEstimator(new QuotaOptions { MaxResultBytes = 1234 });
        Assert.Equal(1234, estimator.LimitBytes);
    }

    [Fact]
    public void ParseReplayInfo_reads_engine_json()
    {
        var info = RenderSizeEstimator.ParseReplayInfo(
            "some log line\n{\"found\":true,\"beatmapMd5\":\"abc\",\"durationSeconds\":38.99,\"rate\":1.5}\n");

        Assert.NotNull(info);
        Assert.True(info!.Found);
        Assert.Equal(38.99, info.DurationSeconds, 2);
        Assert.Equal(1.5, info.Rate, 2);
    }

    [Fact]
    public void ParseReplayInfo_handles_not_found()
    {
        var info = RenderSizeEstimator.ParseReplayInfo("{\"found\":false,\"beatmapMd5\":\"abc\"}");
        Assert.NotNull(info);
        Assert.False(info!.Found);
    }
}

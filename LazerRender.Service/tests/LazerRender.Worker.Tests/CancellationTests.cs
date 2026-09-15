using LazerRender.Api.Services;
using Xunit;

namespace LazerRender.Worker.Tests;

public sealed class CancellationTests
{
    [Fact]
    public void RequestCancel_cancels_registered_token()
    {
        var service = new JobCancellationService();

        var token = service.GetToken("job-1");
        Assert.False(token.IsCancellationRequested);

        service.RequestCancel("job-1");
        Assert.True(token.IsCancellationRequested);

        service.Clear("job-1");
    }

    [Fact]
    public void GetToken_returns_same_token_per_job()
    {
        var service = new JobCancellationService();
        var first = service.GetToken("job-2");
        var second = service.GetToken("job-2");
        Assert.Equal(first, second);
    }
}

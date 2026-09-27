using LazerRender.Contracts;
using Xunit;

namespace LazerRender.Worker.Tests;

public sealed class JobStatusTests
{
    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Rejected)]
    [InlineData(JobStatus.Removed)]
    public void Terminal_statuses_are_terminal(JobStatus status) => Assert.True(status.IsTerminal());

    [Theory]
    [InlineData(JobStatus.Uploaded)]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Claimed)]
    [InlineData(JobStatus.Rendering)]
    [InlineData(JobStatus.Finalizing)]
    [InlineData(JobStatus.Cancelling)]
    public void In_flight_statuses_are_not_terminal(JobStatus status) => Assert.False(status.IsTerminal());
}

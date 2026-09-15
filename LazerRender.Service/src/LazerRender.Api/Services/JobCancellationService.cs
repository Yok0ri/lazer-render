using System.Collections.Concurrent;

namespace LazerRender.Api.Services;

/// <summary>
/// Holds a cancellation source per active job so an API request can abort a running render.
/// The worker links each job's token into the process it supervises.
/// </summary>
public sealed class JobCancellationService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> sources = new();

    public CancellationToken GetToken(string jobId) =>
        sources.GetOrAdd(jobId, _ => new CancellationTokenSource()).Token;

    public void RequestCancel(string jobId)
    {
        if (sources.TryGetValue(jobId, out var cts))
            cts.Cancel();
    }

    public void Clear(string jobId)
    {
        if (sources.TryRemove(jobId, out var cts))
            cts.Dispose();
    }
}

namespace LazerRender.Api.Services;

/// <summary>
/// Serialises job creation so the quota check and the insert cannot interleave.
///
/// <see cref="QuotaService.ValidateAsync"/> counts active and daily jobs, and the caller inserts the new
/// job later — two statements, so concurrent requests from one user can each pass the cap. The service
/// is documented as single-instance (one render worker, one GPU), so a process-local gate is sufficient
/// and much cheaper than a database write lock. The quota itself stays advisory: the worker's atomic
/// claim remains the real gate on how much renders at once.
/// </summary>
public sealed class JobCreationGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Holds the gate until the returned handle is disposed.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new Handle(gate);
    }

    private sealed class Handle(SemaphoreSlim gate) : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            if (released)
                return;

            released = true;
            gate.Release();
        }
    }
}

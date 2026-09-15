namespace LazerRender.Contracts;

/// <summary>
/// Lifecycle state of a render job. See plans/phase5-web-service-design.md for the
/// transition table.
/// </summary>
public enum JobStatus
{
    Uploaded = 0,
    Validating = 1,
    Queued = 2,
    Claimed = 3,
    Rendering = 4,
    Finalizing = 5,
    Stored = 6,
    Completed = 7,
    Failed = 8,
    Cancelling = 9,
    Cancelled = 10,
    Rejected = 11,
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Rejected;

    public static bool IsCancellable(this JobStatus status) =>
        status is JobStatus.Queued or JobStatus.Claimed or JobStatus.Rendering or JobStatus.Finalizing;

    public static bool IsRetryable(this JobStatus status) =>
        status is JobStatus.Failed or JobStatus.Cancelled;
}

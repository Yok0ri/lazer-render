namespace LazerRender.Api.Configuration;

/// <summary>
/// Simple per-user abuse protection. Kept small and configurable rather than a billing model.
/// </summary>
public sealed class QuotaOptions
{
    public const string SectionName = "Quota";

    public int MaxActiveJobs { get; set; } = 1;
    public int MaxJobsPerDay { get; set; } = 10;
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    public double MaxDurationSeconds { get; set; } = 3600;
    public int DefaultMaxAttempts { get; set; } = 3;
    public int ResultRetentionDays { get; set; } = 7;

    /// <summary>Conservative video bitrate model (bits per pixel). 0.20 bpp ≈ 25 Mbps at 1080p60,
    /// which covers CRF/QP-18 encodes including beatmaps with background video.</summary>
    public double SizeBitsPerPixel { get; set; } = 0.20;

    /// <summary>Multiplier applied on top of the raw video + audio estimate (container overhead).</summary>
    public double SizeOverhead { get; set; } = 1.15;

    /// <summary>Hard cap on the estimated output size in bytes. 0 = auto: one hour of 1080p60.</summary>
    public long MaxResultBytes { get; set; }
}

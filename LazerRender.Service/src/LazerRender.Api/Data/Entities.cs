using LazerRender.Contracts;

namespace LazerRender.Api.Data;

public sealed class UserEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long OsuUserId { get; set; }
    public string Username { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string? CountryCode { get; set; }
    public string Role { get; set; } = "user";
    public bool IsAllowed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    public OAuthTokenEntity? OAuthToken { get; set; }
    public List<JobEntity> Jobs { get; set; } = new();
}

public sealed class OAuthTokenEntity
{
    public string UserId { get; set; } = "";
    public string RefreshTokenEncrypted { get; set; } = "";
    public string Scopes { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }

    public UserEntity? User { get; set; }
}

public sealed class JobEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerUserId { get; set; } = "";
    public int DisplayNumber { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Uploaded;

    public string? ReplayPath { get; set; }
    public string? ReplayMd5 { get; set; }
    public string? PlayerUsername { get; set; }
    public string? MapTitle { get; set; }
    public string? MapArtist { get; set; }
    public string? MapCreator { get; set; }
    public string? MapVersion { get; set; }
    public double? MapStars { get; set; }
    public double? SongLength { get; set; }
    public string? Mods { get; set; }
    public double? Accuracy { get; set; }
    public string? SkinName { get; set; }
    public string RenderConfigJson { get; set; } = "{}";
    public EncoderKind Encoder { get; set; } = EncoderKind.Cpu;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 60;
    public double? Duration { get; set; }

    public string? Phase { get; set; }
    public long Frame { get; set; }
    public long? Total { get; set; }
    public double FpsNow { get; set; }
    public string? ErrorMessage { get; set; }

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;

    public string? OutputPath { get; set; }
    public long? ResultSize { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? RetainedUntil { get; set; }

    public UserEntity? Owner { get; set; }
}

public sealed class SkinEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? UploadedBy { get; set; }
    public string? ArchiveHash { get; set; }
    public string? StoragePath { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BeatmapCacheEntity
{
    public string Md5 { get; set; } = "";
    public bool Imported { get; set; }
    public DateTimeOffset? DownloadedAt { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Creator { get; set; }
    public string? Version { get; set; }
    public double? Stars { get; set; }
}

public sealed class PresetEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerUserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public UserEntity? Owner { get; set; }
}

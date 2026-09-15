namespace LazerRender.Contracts;

public sealed record JobDto(
    string Id,
    int DisplayNumber,
    string OwnerUserId,
    string? OwnerUsername,
    string Status,
    string? PlayerUsername,
    string? BeatmapMd5,
    string? SkinName,
    string Encoder,
    int Width,
    int Height,
    int Fps,
    double? Duration,
    string? Phase,
    long Frame,
    long? Total,
    double FpsNow,
    string? ErrorMessage,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long? ResultSize,
    bool ResultAvailable,
    int? QueuePosition,
    int? QueueLength,
    string? MapTitle,
    string? MapArtist,
    string? MapCreator,
    string? MapVersion,
    double? MapStars,
    double? SongLength,
    string? Mods,
    double? Accuracy);

public sealed record JobCreatedResponse(string Id, int DisplayNumber, string Status);

public sealed record JobListResponse(IReadOnlyList<JobDto> Items, int Total, int Offset);

public sealed record ApiUserDto(
    string Id,
    long OsuUserId,
    string Username,
    string? AvatarUrl,
    string Role,
    DateTimeOffset CreatedAt);

public sealed record PresetDto(
    string Id,
    string Name,
    string ConfigJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PresetListResponse(IReadOnlyList<PresetDto> Items);

public sealed record AdminUserDto(
    string Id,
    long OsuUserId,
    string Username,
    string Role,
    bool IsAllowed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record ErrorResponse(string Error, string? Detail = null);

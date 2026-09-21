using LazerRender.Api.Data;
using LazerRender.Contracts;

namespace LazerRender.Api.Services;

public static class JobMapper
{
    public static JobDto ToDto(
        JobEntity j,
        string? ownerUsername = null,
        int? queuePosition = null,
        int? queueLength = null) => new(
        j.Id,
        j.DisplayNumber,
        j.OwnerUserId,
        ownerUsername,
        j.Status.ToString().ToLowerInvariant(),
        j.PlayerUsername,
        j.ReplayMd5,
        j.SkinName,
        j.Encoder.Display(),
        j.Width,
        j.Height,
        j.Fps,
        j.Duration,
        j.Phase,
        j.Frame,
        j.Total,
        j.FpsNow,
        j.ErrorMessage,
        j.Attempts,
        j.MaxAttempts,
        j.CreatedAt,
        j.StartedAt,
        j.FinishedAt,
        j.ResultSize,
        j.OutputPath is not null,
        queuePosition,
        queueLength,
        j.MapTitle,
        j.MapArtist,
        j.MapCreator,
        j.MapVersion,
        j.MapStars,
        j.SongLength,
        j.Mods,
        j.Accuracy);
}

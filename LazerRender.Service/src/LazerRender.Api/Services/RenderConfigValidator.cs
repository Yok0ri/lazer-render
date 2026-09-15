using LazerRender.Contracts;

namespace LazerRender.Api.Services;

/// <summary>
/// Validates a <see cref="RenderConfig"/> against the exact schema in WEB_GUI_GUIDE.md.
/// Values outside the documented ranges are rejected rather than clamped.
/// </summary>
public static class RenderConfigValidator
{
    private static readonly string[] HudVisibilityValues = { "never", "hiddengameplay", "always" };
    private static readonly string[] PlayfieldBorderValues = { "none", "corners", "full" };

    /// <summary>
    /// Leaderboard scopes accepted by <see cref="RenderConfig.LeaderboardScope"/>. Mirrors the engine's
    /// <c>LeaderboardScopes.All</c> (keep these in sync).
    /// </summary>
    private static readonly string[] LeaderboardScopeValues = { "global", "country", "friend", "team" };

    /// <summary>
    /// The HUD component keys accepted by <see cref="RenderConfig.Hud"/>. Mirrors the engine's
    /// <c>HudVisibilityFilter.AllKeys</c> (keep these in sync).
    /// </summary>
    private static readonly string[] HudComponentKeys =
    {
        "hp", "combo", "score", "keyoverlay", "accuracy", "pp", "hiterror", "song-progress",
        "unstable-rate", "judgements", "mods", "aim-error", "rank", "longest-combo", "scoreboard",
        "bpm", "cps", "player-name", "avatar", "flags", "spectators", "cosmetic",
    };
    private static readonly int[] SupportedFpsValues = { 30, 60, 90, 120 };
    private static readonly (int Width, int Height)[] SupportedResolutions =
    {
        (1280, 720),
        (1920, 1080),
        (2560, 1440),
        (3840, 2160),
    };

    public static IReadOnlyList<string> Validate(RenderConfig config)
    {
        var errors = new List<string>();

        if (!SupportedFpsValues.Contains(config.Fps))
            errors.Add("fps must be one of 30, 60, 90 or 120.");

        if (!SupportedResolutions.Contains((config.Width, config.Height)))
            errors.Add("resolution must be one of 1280x720, 1920x1080, 2560x1440 or 3840x2160.");

        if (config.DimLevel is < 0 or > 1)
            errors.Add("dimLevel must be between 0 and 1.");

        if (config.BlurLevel is < 0 or > 1)
            errors.Add("blurLevel must be between 0 and 1.");

        if (config.Parallax is < 0 or > 2)
            errors.Add("parallax must be between 0 and 2.");

        if (config.ComboColourNormalisation is < 0 or > 1)
            errors.Add("comboColourNormalisation must be between 0 and 1.");

        if (!HudVisibilityValues.Contains(config.HudVisibility))
            errors.Add("hudVisibility must be never, hiddengameplay or always.");

        if (config.HudScale is < 0.1 or > 5)
            errors.Add("hudScale must be between 0.1 and 5.");

        if (config.CursorSize is < 0.1 or > 2)
            errors.Add("cursorSize must be between 0.1 and 2.");

        if (!PlayfieldBorderValues.Contains(config.PlayfieldBorder))
            errors.Add("playfieldBorder must be none, corners or full.");

        if (!LeaderboardScopeValues.Contains(config.LeaderboardScope))
            errors.Add("leaderboardScope must be global, country, friend or team.");

        if (config.ReplayAnalysisLength is < 200 or > 2000)
            errors.Add("replayAnalysisLength must be between 200 and 2000.");

        if (config.MotionBlur is < 0 or > 32)
            errors.Add("motionBlur must be between 0 and 32.");

        if (config.Hud != null)
        {
            foreach (string key in config.Hud)
            {
                if (!HudComponentKeys.Contains(key))
                {
                    errors.Add($"hud contains an unknown HUD component \"{key}\".");
                    break;
                }
            }
        }

        if (config.Duration is <= 0)
            errors.Add("duration must be positive when provided.");

        return errors;
    }
}

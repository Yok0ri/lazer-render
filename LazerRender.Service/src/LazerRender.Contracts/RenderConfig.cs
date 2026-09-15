using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LazerRender.Contracts;

/// <summary>
/// The per-render settings document accepted by LazerRender's <c>--render-config</c> flag.
/// Mirrors the schema in WEB_GUI_GUIDE.md. Omitted keys use the recorder's defaults, so the
/// API may always serialize a complete document.
/// </summary>
public sealed class RenderConfig
{
    public int Fps { get; set; } = 60;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;

    public double DimLevel { get; set; } = 0.7;
    public double BlurLevel { get; set; }
    public double Parallax { get; set; } = 1.0;

    public bool Storyboard { get; set; } = true;
    public bool Video { get; set; } = true;
    public bool BeatmapSkins { get; set; } = true;
    public bool BeatmapColours { get; set; } = true;
    public bool BeatmapHitsounds { get; set; } = true;
    public double ComboColourNormalisation { get; set; } = 0.2;

    public string HudVisibility { get; set; } = "always";
    public double HudScale { get; set; } = 1.0;

    public bool SnakingIn { get; set; } = true;
    public bool SnakingOut { get; set; } = true;
    public bool HitAnimations { get; set; } = true;
    public bool HitLighting { get; set; }
    public bool CursorTrail { get; set; } = true;
    public bool CursorRipples { get; set; }
    public double CursorSize { get; set; } = 1.0;
    public string PlayfieldBorder { get; set; } = "none";

    public bool ShowClickMarkers { get; set; }
    public bool ShowFrameMarkers { get; set; }
    public bool ShowCursorPath { get; set; }
    public bool HideGameplayCursor { get; set; }
    public int ReplayAnalysisLength { get; set; } = 800;

    public int MotionBlur { get; set; }

    /// <summary>
    /// When true, the recorder fades to black at the end of the replay and stops instead of
    /// transitioning to the results screen.
    /// </summary>
    public bool DisableResultScreen { get; set; }

    /// <summary>
    /// Which beatmap leaderboard the recorder warms for the scoreboard: <c>global</c> (default),
    /// <c>country</c>, <c>friend</c> or <c>team</c>. Mirrors the scope a player can pick in song
    /// select; requires the render to be signed into the osu! API to have any effect.
    /// </summary>
    public string LeaderboardScope { get; set; } = "global";

    /// <summary>
    /// When present, switches the recorder into whitelist mode: only the listed HUD components are
    /// shown, everything else is hidden. Keys are the component identifiers defined by the engine's
    /// <c>HudVisibilityFilter</c> (<c>hp</c>, <c>combo</c>, <c>score</c>, ...). An omitted list keeps
    /// the default "show everything" behaviour; an explicit empty list hides every HUD element.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Hud { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Skin { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Duration { get; set; }
}

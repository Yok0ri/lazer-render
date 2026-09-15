// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;

namespace LazerRender
{
    /// <summary>
    /// The operation requested from the command line. LazerRender is now a headless server
    /// component, so a single invocation performs exactly one of: render a replay, import a
    /// beatmap package, or import a skin package.
    /// </summary>
    public enum RunMode
    {
        /// <summary>Render a replay (<c>--replay <path.osr></c>).</summary>
        Record,

        /// <summary>Import a beatmap package into the persistent Realm database (<c>--import-map</c>).</summary>
        ImportMap,

        /// <summary>Import a legacy skin package into the persistent Realm database (<c>--import-skin</c>).</summary>
        ImportSkin,

        /// <summary>Purge beatmaps/skins from the persistent Realm database (<c>--purge</c>).</summary>
        Purge,

        /// <summary>Look up beatmap metadata by MD5 hash and print it as JSON (<c>--map-info</c>).</summary>
        MapInfo,

        /// <summary>Print replay render metadata (duration + speed) as JSON (<c>--replay-info</c>).</summary>
        ReplayInfo,
    }

    /// <summary>
    /// Video encoder backend selected by <c>--encoder</c>. <see cref="Cpu"/> keeps the original
    /// libx264 software fallback; the other values offload encoding to a GPU media engine.
    /// </summary>
    public enum EncoderKind
    {
        /// <summary>Software libx264 encoding (default).</summary>
        Cpu,

        /// <summary>AMD VAAPI <c>h264_vaapi</c> encoding.</summary>
        Amd,

        /// <summary>NVIDIA NVENC <c>h264_nvenc</c> encoding.</summary>
        Nvidia,

        /// <summary>Intel Quick Sync Video <c>h264_qsv</c> encoding.</summary>
        Intel,
    }

    /// <summary>
    /// Command-line options for a LazerRender run. Only the fields relevant to the selected
    /// <see cref="Mode"/> are populated by the argument parser.
    /// </summary>
    public sealed class RecordOptions
    {
        /// <summary>The operation to perform. Determined by which of <c>--replay</c>, <c>--import-map</c>
        /// or <c>--import-skin</c> is present.</summary>
        public RunMode Mode { get; set; } = RunMode.Record;

        /// <summary>Path to the replay file (.osr) to render. Only used when <see cref="Mode"/> is
        /// <see cref="RunMode.Record"/>.</summary>
        public string ReplayPath { get; set; } = string.Empty;

        /// <summary>The beatmap MD5 hash to look up. Only used when <see cref="Mode"/> is
        /// <see cref="RunMode.MapInfo"/>.</summary>
        public string MapInfoHash { get; set; } = string.Empty;

        /// <summary>Path to the replay file (.osr) to inspect. Only used when <see cref="Mode"/> is
        /// <see cref="RunMode.ReplayInfo"/>.</summary>
        public string ReplayInfoPath { get; set; } = string.Empty;

        /// <summary>Path to a beatmap package (.osu or .osz) to import. Only used when
        /// <see cref="Mode"/> is <see cref="RunMode.ImportMap"/>.</summary>
        public string ImportMapPath { get; set; } = string.Empty;

        /// <summary>Path to a legacy skin package (.osk) to import. Only used when
        /// <see cref="Mode"/> is <see cref="RunMode.ImportSkin"/>.</summary>
        public string ImportSkinPath { get; set; } = string.Empty;

        /// <summary>Optional name of a skin already present in the Realm database to apply while
        /// recording. When <c>null</c> or empty, the built-in osu! "argon" pro skin is used (with
        /// beatmap-bundled skins still applied as a fallback layer).</summary>
        public string? SkinName { get; set; }

        /// <summary>Video encoder backend (<c>--encoder <cpu|amd|nvidia|intel></c>). Defaults to
        /// <see cref="EncoderKind.Cpu"/> (libx264).</summary>
        public EncoderKind Encoder { get; set; } = EncoderKind.Cpu;

        /// <summary>Which asset classes to purge (<c>--purge <beatmaps|skins|all></c>). Defaults to
        /// <c>all</c>. Only used when <see cref="Mode"/> is <see cref="RunMode.Purge"/>.</summary>
        public string PurgeTarget { get; set; } = @"all";

        /// <summary>When the replay's beatmap MD5 hash is not found in the local Realm database,
        /// automatically download and import it from a public mirror (<c>--download-missing</c>).</summary>
        public bool DownloadMissing { get; set; }

        /// <summary>Additional UI scale multiplier (<c>--hud-scale <multiplier></c>), mirroring
        /// osu!lazer's in-game UI scale setting. When <c>null</c> or <c>1</c>, the lazer-native UI
        /// scale is used (the renderer already lays the HUD out at lazer's 1024x768 reference and
        /// scales it to the output resolution). Values above/below 1 grow/shrink the whole HUD
        /// uniformly while the 4:3 playfield keeps its size.</summary>
        public double? HudScale { get; set; }

        /// <summary>Directory where the encoded video is written (record mode only).</summary>
        public string OutputDirectory { get; set; } = "frames";

        /// <summary>Directory used for the persistent lazer storage (realm DB + files).</summary>
        public string StorageDirectory { get; set; } = "storage";

        /// <summary>Fixed render window width, in pixels.</summary>
        public int Width { get; set; } = 1280;

        /// <summary>Fixed render window height, in pixels.</summary>
        public int Height { get; set; } = 720;

        /// <summary>Recorded draw rate. Simulation remains deterministic at ~60Hz.</summary>
        public int Fps { get; set; } = 60;

        /// <summary>Optional duration of gameplay to record, in seconds. When <c>null</c>, the recorder
        /// keeps rendering until the replay's final input frame (plus the results tail).</summary>
        public double? DurationSeconds { get; set; }

        /// <summary>
        /// Per-render setting overrides keyed by <see cref="SettingDescriptor.Key"/>. Populated from
        /// individual CLI flags and/or <c>--render-config</c> JSON, then applied by
        /// <see cref="SettingsEngine.Apply"/>.
        /// </summary>
        public Dictionary<string, object> Settings { get; } = new Dictionary<string, object>();

        /// <summary>
        /// HUD components to keep (<c>--hud</c>), keyed by the <see cref="HudVisibilityFilter"/> component
        /// keys. When <see cref="HudSpecified"/> is set this switches the recorder into whitelist mode:
        /// every HUD component not listed here is hidden. When unset, all components are shown.
        /// </summary>
        public HashSet<string> HudComponents { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Whether the HUD whitelist was provided at all. This is what distinguishes "no whitelist"
        /// (show everything, the recorder's default) from an explicitly empty whitelist (hide every
        /// HUD element). Set by <c>--hud</c> and by the presence of a <c>hud</c> JSON key.
        /// </summary>
        public bool HudSpecified { get; set; }

        /// <summary>
        /// When true, the recorder fades the gameplay to black at the end of the replay and stops
        /// instead of transitioning to the results screen (<c>--disable-result-screen</c>).
        /// </summary>
        public bool DisableResultScreen { get; set; }

        /// <summary>
        /// Which beatmap leaderboard to warm for the scoreboard (<c>--leaderboard-scope</c>, JSON
        /// <c>leaderboardScope</c>). One of <see cref="LeaderboardScopes.All"/>; defaults to
        /// <c>global</c>. Only meaningful when a user token signs the engine in.
        /// </summary>
        public string LeaderboardScope { get; set; } = LeaderboardScopes.Global;

        /// <summary>Number of frames FFmpeg's <c>tmix</c> filter blends together for motion blur
        /// (<c>--motion-blur <frames></c>). <c>0</c> disables the filter.</summary>
        public int MotionBlurFrames { get; set; }

        /// <summary>Optional osu! API v2 bearer token used to fetch the player's avatar for the
        /// results screen (<c>--avatar-api-key</c>, or the <c>OSU_API_KEY</c> environment variable).
        /// When unset, the default avatar placeholder is used. A client-credentials token is
        /// sufficient for this.</summary>
        public string? AvatarApiKey { get; set; }

        /// <summary>
        /// Optional osu! API v2 <em>user</em> access token (<c>--osu-user-token</c>). When set, the
        /// recorder signs lazer's API provider in before the game loads, which is what allows online
        /// beatmap leaderboards (the results-screen scoreboard and the <c>scoreboard</c> HUD element)
        /// to fetch scores. A client-credentials token cannot be used here: lazer validates the token
        /// against <c>/me</c>, which requires a user context (<c>identify</c> scope).
        /// </summary>
        public string? OsuUserToken { get; set; }

        /// <summary>Validity of <see cref="OsuUserToken"/> in seconds
        /// (<c>--osu-user-token-expires-in</c>). Only used to build the token lazer stores.</summary>
        public long OsuUserTokenExpiresIn { get; set; } = 3600;
    }
}

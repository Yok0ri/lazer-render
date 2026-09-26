// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using osu.Framework.Configuration;
using osu.Framework.Logging;
using osu.Game.Configuration;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.UI;

namespace LazerRender
{
    /// <summary>Which config manager owns a setting.</summary>
    public enum SettingTarget
    {
        /// <summary>Global <see cref="OsuConfigManager"/> (<see cref="OsuSetting"/>).</summary>
        Global,

        /// <summary>osu! ruleset <see cref="OsuRulesetConfigManager"/> (<see cref="OsuRulesetSetting"/>).</summary>
        Ruleset,
    }

    /// <summary>The value type of a setting, used for parsing, validation and dispatch.</summary>
    public enum SettingValueKind
    {
        Bool,
        Float,
        Double,
        Int,
        Enum,
    }

    /// <summary>
    /// A single render setting: its CLI flag, JSON key, backing config value, type and validation
    /// range. The catalog built in <see cref="SettingsCatalog"/> is the single source of truth used
    /// by CLI parsing, <c>--render-config</c> JSON parsing and <see cref="SettingsEngine.Apply"/>.
    /// </summary>
    public sealed class SettingDescriptor
    {
        /// <summary>Stable key used in the JSON config document (e.g. <c>dimLevel</c>).</summary>
        public required string Key { get; init; }

        /// <summary>CLI flag stem (e.g. <c>dim-level</c> → <c>--dim-level</c>).</summary>
        public required string Flag { get; init; }

        /// <summary>Which config manager the setting belongs to.</summary>
        public required SettingTarget Target { get; init; }

        /// <summary>The enum member to pass to the config manager (<see cref="OsuSetting"/> or <see cref="OsuRulesetSetting"/>).</summary>
        public required object Lookup { get; init; }

        /// <summary>Value kind, driving parse + dispatch.</summary>
        public required SettingValueKind Kind { get; init; }

        public double? Min { get; init; }
        public double? Max { get; init; }
        public double? Step { get; init; }

        /// <summary>Upstream default, applied when the setting is not overridden.</summary>
        public required object DefaultValue { get; init; }

        /// <summary>For enum settings: accepted name → enum value (case-insensitive names).</summary>
        public IReadOnlyDictionary<string, object>? EnumValues { get; init; }

        /// <summary>
        /// When true, the stored config value is the logical inverse of the user-facing value.
        /// Used only by <c>video</c> (<see cref="OsuSetting.PreferNoVideo"/>).
        /// </summary>
        public bool Inverted { get; init; }

        public required string Description { get; init; }
    }

    /// <summary>The full settings catalog (WISHLIST steps 2 & 3, minus the cut audio offset).</summary>
    public static class SettingsCatalog
    {
        public static readonly IReadOnlyList<SettingDescriptor> All = Build();

        public static SettingDescriptor? FindByFlag(string flag)
            => All.FirstOrDefault(d => d.Flag.Equals(flag, StringComparison.OrdinalIgnoreCase));

        public static SettingDescriptor? FindByKey(string key)
            => All.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        private static SettingDescriptor global(string key, string flag, OsuSetting lookup, SettingValueKind kind, object defaultValue, string description, double? min = null, double? max = null, double? step = null, IReadOnlyDictionary<string, object>? enumValues = null, bool inverted = false)
            => new SettingDescriptor
            {
                Key = key,
                Flag = flag,
                Target = SettingTarget.Global,
                Lookup = lookup,
                Kind = kind,
                DefaultValue = defaultValue,
                Description = description,
                Min = min,
                Max = max,
                Step = step,
                EnumValues = enumValues,
                Inverted = inverted,
            };

        private static SettingDescriptor ruleset(string key, string flag, OsuRulesetSetting lookup, SettingValueKind kind, object defaultValue, string description, double? min = null, double? max = null, double? step = null, IReadOnlyDictionary<string, object>? enumValues = null, bool inverted = false)
            => new SettingDescriptor
            {
                Key = key,
                Flag = flag,
                Target = SettingTarget.Ruleset,
                Lookup = lookup,
                Kind = kind,
                DefaultValue = defaultValue,
                Description = description,
                Min = min,
                Max = max,
                Step = step,
                EnumValues = enumValues,
                Inverted = inverted,
            };

        private static List<SettingDescriptor> Build() => new List<SettingDescriptor>
        {
            // --- Global gameplay / visual settings (OsuConfigManager) ---
            global("dimLevel", "dim-level", OsuSetting.DimLevel, SettingValueKind.Double, 0.7, @"Background dim (0..1).", 0, 1, 0.01),
            global("blurLevel", "blur-level", OsuSetting.BlurLevel, SettingValueKind.Double, 0.0, @"Background blur (0..1).", 0, 1, 0.01),
            global("parallax", "parallax", OsuSetting.MenuParallaxScale, SettingValueKind.Float, 1.0f, @"Background parallax scale (0..2).", 0, 2, 0.1),
            global("storyboard", "storyboard", OsuSetting.ShowStoryboard, SettingValueKind.Bool, true, @"Show the beatmap storyboard."),
            global("video", "video", OsuSetting.PreferNoVideo, SettingValueKind.Bool, true, @"Show the beatmap background video.", inverted: true),
            global("beatmapSkins", "beatmap-skins", OsuSetting.BeatmapSkins, SettingValueKind.Bool, true, @"Use the beatmap's skin."),
            global("beatmapColours", "beatmap-colours", OsuSetting.BeatmapColours, SettingValueKind.Bool, true, @"Use the beatmap's colours."),
            global("comboColourNormalisation", "combo-colour-normalisation", OsuSetting.ComboColourNormalisationAmount, SettingValueKind.Float, 0.2f, @"Combo colour normalisation amount (0..1).", 0, 1, 0.01),
            global("beatmapHitsounds", "beatmap-hitsounds", OsuSetting.BeatmapHitsounds, SettingValueKind.Bool, true, @"Play the beatmap's hitsounds."),
            global("cursorSize", "cursor-size", OsuSetting.GameplayCursorSize, SettingValueKind.Float, 1.0f, @"Gameplay cursor size (0.1..2).", 0.1, 2, 0.01),
            global("hudVisibility", "hud-visibility", OsuSetting.HUDVisibilityMode, SettingValueKind.Enum, HUDVisibilityMode.Always, @"HUD visibility: never, hiddengameplay or always.",
                enumValues: new Dictionary<string, object>
                {
                    [@"never"] = HUDVisibilityMode.Never,
                    [@"hiddengameplay"] = HUDVisibilityMode.HideDuringGameplay,
                    [@"always"] = HUDVisibilityMode.Always,
                }),
            global("hitLighting", "hit-lighting", OsuSetting.HitLighting, SettingValueKind.Bool, false, @"Show hit lighting (click aftereffects)."),
            global("starFountains", "star-fountains", OsuSetting.StarFountains, SettingValueKind.Bool, false, @"Show star fountains during kiais."),

            // --- osu! ruleset settings (OsuRulesetConfigManager) ---
            ruleset("snakingIn", "snaking-in", OsuRulesetSetting.SnakingInSliders, SettingValueKind.Bool, true, @"Snake sliders in."),
            ruleset("snakingOut", "snaking-out", OsuRulesetSetting.SnakingOutSliders, SettingValueKind.Bool, true, @"Snake sliders out."),
            ruleset("hitAnimations", "hit-animations", OsuRulesetSetting.HitAnimations, SettingValueKind.Bool, true, @"Show hit animations."),
            ruleset("cursorTrail", "cursor-trail", OsuRulesetSetting.ShowCursorTrail, SettingValueKind.Bool, true, @"Show the cursor trail."),
            ruleset("cursorRipples", "cursor-ripples", OsuRulesetSetting.ShowCursorRipples, SettingValueKind.Bool, false, @"Show cursor ripples."),
            ruleset("playfieldBorder", "playfield-border", OsuRulesetSetting.PlayfieldBorderStyle, SettingValueKind.Enum, PlayfieldBorderStyle.None, @"Playfield border: none, corners or full.",
                enumValues: new Dictionary<string, object>
                {
                    [@"none"] = PlayfieldBorderStyle.None,
                    [@"corners"] = PlayfieldBorderStyle.Corners,
                    [@"full"] = PlayfieldBorderStyle.Full,
                }),
            ruleset("hideGameplayCursor", "hide-gameplay-cursor", OsuRulesetSetting.ReplayCursorHideEnabled, SettingValueKind.Bool, false, @"Hide the gameplay cursor."),
            ruleset("showClickMarkers", "show-click-markers", OsuRulesetSetting.ReplayClickMarkersEnabled, SettingValueKind.Bool, false, @"Show replay click markers."),
            ruleset("showFrameMarkers", "show-frame-markers", OsuRulesetSetting.ReplayFrameMarkersEnabled, SettingValueKind.Bool, false, @"Show replay frame markers."),
            ruleset("showCursorPath", "show-cursor-path", OsuRulesetSetting.ReplayCursorPathEnabled, SettingValueKind.Bool, false, @"Show the replay cursor path."),
            ruleset("replayAnalysisLength", "replay-analysis-length", OsuRulesetSetting.ReplayAnalysisDisplayLength, SettingValueKind.Int, 800, @"Replay cursor path display length (200..2000).", 200, 2000),
        };
    }

    /// <summary>Parses, validates and applies <see cref="SettingDescriptor"/> values.</summary>
    public static class SettingsEngine
    {
        /// <summary>Parses and validates a CLI string value for <paramref name="d"/>.</summary>
        public static bool TryParseCli(SettingDescriptor d, string? raw, out object value, out string? error)
        {
            value = d.DefaultValue;
            error = null;

            try
            {
                switch (d.Kind)
                {
                    case SettingValueKind.Bool:
                        value = parseBool(raw);
                        break;

                    case SettingValueKind.Float:
                        value = parseFloat(d, raw);
                        break;

                    case SettingValueKind.Double:
                        value = parseDouble(d, raw);
                        break;

                    case SettingValueKind.Int:
                        value = parseInt(d, raw);
                        break;

                    case SettingValueKind.Enum:
                        value = parseEnum(d, raw);
                        break;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>Parses and validates a JSON value for <paramref name="d"/>.</summary>
        public static bool TryParseJson(SettingDescriptor d, JsonElement element, out object value, out string? error)
        {
            value = d.DefaultValue;
            error = null;

            try
            {
                switch (d.Kind)
                {
                    case SettingValueKind.Bool:
                        value = element.GetBoolean();
                        break;

                    case SettingValueKind.Float:
                        value = parseFloat(d, element.GetDouble().ToString(CultureInfo.InvariantCulture));
                        break;

                    case SettingValueKind.Double:
                        value = parseDouble(d, element.GetDouble().ToString(CultureInfo.InvariantCulture));
                        break;

                    case SettingValueKind.Int:
                        value = parseInt(d, element.GetInt32().ToString(CultureInfo.InvariantCulture));
                        break;

                    case SettingValueKind.Enum:
                        value = parseEnum(d, element.GetString());
                        break;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// Applies every catalog setting to the config managers, using <paramref name="overrides"/>
        /// when present and the catalog default otherwise. This preserves the existing guarantee that
        /// each run sets every value explicitly so persisted state cannot leak between runs.
        /// </summary>
        public static void Apply(OsuConfigManager localConfig, OsuRulesetConfigManager? rulesetConfig, IReadOnlyDictionary<string, object> overrides)
        {
            foreach (SettingDescriptor d in SettingsCatalog.All)
            {
                object value = overrides.TryGetValue(d.Key, out object? v) ? v : d.DefaultValue;

                Logger.Log($@"SettingsEngine: applying {d.Key} = {value} ({(d.Target == SettingTarget.Global ? @"global" : @"ruleset")})");

                applyOne(localConfig, rulesetConfig, d, value);
            }
        }

        private static void applyOne(OsuConfigManager localConfig, OsuRulesetConfigManager? rulesetConfig, SettingDescriptor d, object value)
        {
            if (d.Target == SettingTarget.Global)
                setValue(localConfig, (OsuSetting)d.Lookup, d, value);
            else if (rulesetConfig != null)
                setValue(rulesetConfig, (OsuRulesetSetting)d.Lookup, d, value);
        }

        private static void setValue<TLookup>(ConfigManager<TLookup> config, TLookup lookup, SettingDescriptor d, object value)
            where TLookup : struct, Enum
        {
            switch (d.Kind)
            {
                case SettingValueKind.Bool:
                    bool b = (bool)value;
                    if (d.Inverted)
                        b = !b;
                    config.SetValue(lookup, b);
                    break;

                case SettingValueKind.Float:
                    config.SetValue(lookup, (float)value);
                    break;

                case SettingValueKind.Double:
                    config.SetValue(lookup, (double)value);
                    break;

                case SettingValueKind.Int:
                    config.SetValue(lookup, (int)value);
                    break;

                case SettingValueKind.Enum:
                    setEnumValue(config, lookup, value);
                    break;
            }
        }

        /// <summary>
        /// Enums are boxed with their concrete type, so the generic <c>SetValue<TValue></c> must
        /// be invoked reflectively with the runtime type. This runs once per setting per render, not
        /// per frame, so the reflection cost is irrelevant.
        /// </summary>
        private static void setEnumValue<TLookup>(ConfigManager<TLookup> config, TLookup lookup, object value)
            where TLookup : struct, Enum
        {
            MethodInfo setValue = typeof(ConfigManager<TLookup>)
                .GetMethods()
                .First(m => m.Name == "SetValue" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                .MakeGenericMethod(value.GetType());

            setValue.Invoke(config, new[] { (object)lookup, value });
        }

        private static bool parseBool(string? raw)
        {
            if (raw == null)
                return true;

            return raw.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => throw new ArgumentException($@"Invalid boolean value: {raw}"),
            };
        }

        private static float parseFloat(SettingDescriptor d, string? raw)
        {
            if (raw == null)
                throw new ArgumentException($@"Missing value for --{d.Flag}.");

            float v = float.Parse(raw, CultureInfo.InvariantCulture);
            checkRange(d, v);
            return v;
        }

        private static double parseDouble(SettingDescriptor d, string? raw)
        {
            if (raw == null)
                throw new ArgumentException($@"Missing value for --{d.Flag}.");

            double v = double.Parse(raw, CultureInfo.InvariantCulture);
            checkRange(d, v);
            return v;
        }

        private static int parseInt(SettingDescriptor d, string? raw)
        {
            if (raw == null)
                throw new ArgumentException($@"Missing value for --{d.Flag}.");

            int v = int.Parse(raw, CultureInfo.InvariantCulture);
            checkRange(d, v);
            return v;
        }

        private static object parseEnum(SettingDescriptor d, string? raw)
        {
            if (raw == null)
                throw new ArgumentException($@"Missing value for --{d.Flag}.");

            if (d.EnumValues == null)
                throw new ArgumentException($@"No enum values are defined for {d.Key}.");

            foreach (KeyValuePair<string, object> kv in d.EnumValues)
            {
                if (kv.Key.Equals(raw, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }

            throw new ArgumentException($@"Invalid value '{raw}' for --{d.Flag}. Expected one of: {string.Join(", ", d.EnumValues.Keys)}");
        }

        private static void checkRange(SettingDescriptor d, double value)
        {
            if (d.Min.HasValue && value < d.Min.Value)
                throw new ArgumentException($@"Value {value} for --{d.Flag} is below the minimum {d.Min.Value}.");

            if (d.Max.HasValue && value > d.Max.Value)
                throw new ArgumentException($@"Value {value} for --{d.Flag} is above the maximum {d.Max.Value}.");
        }
    }
}

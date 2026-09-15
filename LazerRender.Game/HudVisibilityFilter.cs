// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Game.Rulesets.Osu.HUD;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.HUD.ClicksPerSecond;
using osu.Game.Screens.Play.HUD.HitErrorMeters;
using osu.Game.Screens.Play.HUD.JudgementCounter;
using osu.Game.Skinning;
using osu.Game.Skinning.Components;

namespace LazerRender
{
    /// <summary>
    /// Hides HUD components by drawable type, matching the same technique already used for the replay
    /// banner/cog and the results-screen toolbar. Best-effort: it matches the standard component
    /// classes (default/Argon/Triangles/legacy) and silently no-ops for bespoke skins that replace a
    /// component with a drawable outside the standard type hierarchy.
    ///
    /// <see cref="ApplyWhitelist"/> hides <em>every</em> HUD component that is not in the requested
    /// keep set (the <c>--hud</c> flag). Only the skinnable HUD component containers are touched, so
    /// the playfield, cursor and ruleset elements are left alone.
    /// </summary>
    public static class HudVisibilityFilter
    {
        public const string HpKey = @"hp";
        public const string ComboKey = @"combo";
        public const string ScoreKey = @"score";
        public const string KeyOverlayKey = @"keyoverlay";
        public const string AccuracyKey = @"accuracy";
        public const string PpKey = @"pp";
        public const string UnstableRateKey = @"unstable-rate";
        public const string SongProgressKey = @"song-progress";
        public const string HitErrorKey = @"hiterror";
        public const string AimErrorKey = @"aim-error";
        public const string ModsKey = @"mods";
        public const string RankKey = @"rank";
        public const string JudgementsKey = @"judgements";
        public const string LongestComboKey = @"longest-combo";
        public const string ScoreboardKey = @"scoreboard";
        public const string BpmKey = @"bpm";
        public const string CpsKey = @"cps";
        public const string PlayerNameKey = @"player-name";
        public const string AvatarKey = @"avatar";
        public const string FlagsKey = @"flags";
        public const string SpectatorsKey = @"spectators";

        /// <summary>
        /// Decorative skin elements with no gameplay meaning (Argon wedge pieces, boxes, text
        /// elements, beatmap attribute readouts, generic sprites). Grouped under a single key so a
        /// render can strip them without exposing each type individually.
        /// </summary>
        public const string CosmeticKey = @"cosmetic";

        /// <summary>
        /// Every selectable HUD component key, in a stable display order. This is the whitelist
        /// vocabulary for <c>--hud</c> (and the service's <c>hud</c> config key).
        /// </summary>
        public static readonly IReadOnlyList<string> AllKeys = new[]
        {
            HpKey,
            ComboKey,
            ScoreKey,
            KeyOverlayKey,
            AccuracyKey,
            PpKey,
            HitErrorKey,
            SongProgressKey,
            UnstableRateKey,
            JudgementsKey,
            ModsKey,
            AimErrorKey,
            RankKey,
            LongestComboKey,
            ScoreboardKey,
            BpmKey,
            CpsKey,
            PlayerNameKey,
            AvatarKey,
            FlagsKey,
            SpectatorsKey,
            CosmeticKey,
        };

        public static bool IsKnownKey(string key) => AllKeys.Contains(key, StringComparer.Ordinal);

        /// <summary>
        /// The skinnable HUD component containers (global + ruleset) under <paramref name="hudRoot"/>.
        /// Callers can subscribe to <see cref="SkinnableContainer.OnComponentsLoaded"/> on these to
        /// re-apply filtering as soon as the (asynchronously loaded) skin components appear.
        /// </summary>
        public static IEnumerable<SkinnableContainer> FindHudComponentContainers(Drawable hudRoot)
            => descendants(hudRoot).OfType<SkinnableContainer>()
                                  .Where(c => c.Lookup.Lookup == GlobalSkinnableContainers.MainHUDComponents);

        /// <summary>
        /// Hides every skinnable HUD component under <paramref name="hudRoot"/> whose type does not
        /// match one of <paramref name="keepKeys"/>, leaving only the whitelisted elements visible.
        /// Fixed (non-skinnable) HUD controls such as the hold-to-quit button are always hidden.
        /// </summary>
        public static void ApplyWhitelist(Drawable hudRoot, IReadOnlyCollection<string> keepKeys)
        {
            var kept = new Dictionary<string, int>();
            var hidden = new Dictionary<string, int>();

            // Only the main HUD components (global + ruleset) are filtered; the playfield skin layer
            // is left untouched so gameplay elements are never affected.
            foreach (SkinnableContainer container in FindHudComponentContainers(hudRoot))
            {
                foreach (ISerialisableDrawable component in container.Components)
                {
                    if (component is not Drawable drawable)
                        continue;

                    string? keptKey = matchAny(keepKeys, drawable);

                    if (keptKey != null)
                    {
                        increment(kept, $@"{keptKey}:{drawable.GetType().Name}");
                        continue;
                    }

                    drawable.Alpha = 0;
                    increment(hidden, drawable.GetType().Name);
                }
            }

            // Fixed HUD controls are not represented in the whitelist (there is no setting for the
            // hold-to-quit button or the mod display, which lives outside the skinnable containers).
            foreach (Drawable d in descendants(hudRoot))
            {
                bool shouldHide = d switch
                {
                    FailingLayer => true,
                    HoldForMenuButton => true,
                    ModDisplay => !keepKeys.Contains(ModsKey, StringComparer.Ordinal),
                    _ => false,
                };

                if (!shouldHide)
                    continue;

                d.Alpha = 0;
                increment(hidden, d.GetType().Name);
            }

            Logger.Log($@"HudVisibilityFilter (--hud): kept {format(kept)}; hid {format(hidden)}");
        }

        private static string? matchAny(IReadOnlyCollection<string> keys, Drawable d)
        {
            foreach (string key in keys)
            {
                if (matches(key, d))
                    return key;
            }

            return null;
        }

        private static bool matches(string key, Drawable d)
        {
            switch (key)
            {
                case HpKey:
                    return d is HealthDisplay;

                case ScoreKey:
                    return d is GameplayScoreCounter;

                // The legacy combo counter does NOT derive from ComboCounter, so both must be matched.
                // LongestComboCounter does derive from it, so it is excluded here to stay selectable
                // on its own.
                case ComboKey:
                    return d is ComboCounter and not LongestComboCounter || d is LegacyDefaultComboCounter;

                case LongestComboKey:
                    return d is LongestComboCounter;

                // AimErrorMeter derives from HitErrorMeter, so it must be excluded here to stay
                // selectable on its own.
                case HitErrorKey:
                    return d is HitErrorMeter and not AimErrorMeter;

                case AimErrorKey:
                    return d is AimErrorMeter;

                case KeyOverlayKey:
                    return d is KeyCounterDisplay;

                case AccuracyKey:
                    return d is GameplayAccuracyCounter;

                // The default and legacy rank displays share no base type.
                case RankKey:
                    return d is DefaultRankDisplay or LegacyRankDisplay;

                case PpKey:
                    return d is PerformancePointsCounter;

                // The default and Argon hit/judgement counters share no base type.
                case JudgementsKey:
                    return d is JudgementCounterDisplay or ArgonJudgementCounterDisplay;

                case UnstableRateKey:
                    return d is UnstableRateCounter;

                case SongProgressKey:
                    return d is SongProgress;

                case ModsKey:
                    return d is ModDisplay or SkinnableModDisplay or ModFlowDisplay;

                case ScoreboardKey:
                    return d is DrawableGameplayLeaderboard;

                case BpmKey:
                    return d is BPMCounter;

                case CpsKey:
                    return d is ClicksPerSecondCounter;

                case PlayerNameKey:
                    return d is PlayerName;

                case AvatarKey:
                    return d is PlayerAvatar;

                case FlagsKey:
                    return d is PlayerFlag or PlayerTeamFlag;

                case SpectatorsKey:
                    return d is SpectatorList;

                // Decorative elements with no gameplay meaning. BeatmapAttributeText is included
                // here (it is a cosmetic star-rating readout, not a functional counter).
                case CosmeticKey:
                    return d is ArgonWedgePiece or BigBlackBox or BoxElement or TextElement or BeatmapAttributeText or SkinnableSprite;

                default:
                    return false;
            }
        }

        private static void increment(Dictionary<string, int> counts, string key)
            => counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;

        private static string format(Dictionary<string, int> counts)
            => counts.Count == 0 ? @"none" : string.Join(@", ", counts.Select(kv => $@"{kv.Key}:{kv.Value}"));

        private static IEnumerable<Drawable> descendants(Drawable root)
        {
            var stack = new Stack<Drawable>(children(root));

            while (stack.Count > 0)
            {
                Drawable current = stack.Pop();

                yield return current;

                foreach (Drawable child in children(current))
                    stack.Push(child);
            }
        }

        private static IEnumerable<Drawable> children(Drawable drawable)
        {
            if (drawable is not CompositeDrawable composite)
                return Array.Empty<Drawable>();

            var property = typeof(CompositeDrawable).GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            return ((property?.GetValue(composite) as IEnumerable)?.OfType<Drawable>() ?? Array.Empty<Drawable>()).ToList();
        }
    }
}

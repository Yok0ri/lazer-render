// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Screens.Play.Leaderboards;

namespace LazerRender
{
    /// <summary>
    /// The beatmap leaderboard scopes a render can ask for (<c>--leaderboard-scope</c> /
    /// <c>leaderboardScope</c>). These mirror what a player can pick in song select: lazer carries the
    /// selected scope into the gameplay leaderboard from <c>PlayerLoader</c>, and the recorder emulates
    /// the same thing by warming <c>LeaderboardManager</c> with this scope.
    ///
    /// <c>Local</c> is deliberately excluded — a render has no local score set to compare against — and
    /// the scope only has any effect when a user token signs the engine in.
    /// </summary>
    public static class LeaderboardScopes
    {
        public const string Global = @"global";
        public const string Country = @"country";
        public const string Friend = @"friend";
        public const string Team = @"team";

        /// <summary>Accepted values, in display order. Also drives the CLI/JSON validation hints.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Global, Country, Friend, Team };

        public static bool IsKnown(string? value) => value != null && All.Contains(Normalise(value), StringComparer.Ordinal);

        public static string Normalise(string value) => value.Trim().ToLowerInvariant();

        /// <summary>Maps a scope key to lazer's enum, falling back to <c>Global</c>.</summary>
        public static BeatmapLeaderboardScope ToLazerScope(string? value) => Normalise(value ?? Global) switch
        {
            Country => BeatmapLeaderboardScope.Country,
            Friend => BeatmapLeaderboardScope.Friend,
            Team => BeatmapLeaderboardScope.Team,
            _ => BeatmapLeaderboardScope.Global,
        };
    }
}

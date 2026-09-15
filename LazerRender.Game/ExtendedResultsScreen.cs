// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Screens;
using osu.Game.Online.API;
using osu.Game.Scoring;
using osu.Game.Screens.Ranking;

namespace LazerRender
{
    /// <summary>
    /// A <see cref="SoloResultsScreen"/> customised for recording: the score card is expanded without
    /// the slide/flair animations and the bottom toolbar is removed.
    ///
    /// The avatar shown in the expanded score panel is not handled here: it reads
    /// <see cref="ScoreInfo.User"/> through the shared <c>DrawableAvatar</c> path, which resolves its
    /// texture from the online asset store. <c>LazerRenderGame</c> populates that store (and the
    /// user's online id) up front, so this screen picks the real avatar up automatically.
    /// </summary>
    public partial class ExtendedResultsScreen : SoloResultsScreen
    {
        public ExtendedResultsScreen(ScoreInfo score)
            : base(score)
        {
            AllowRetry = false;
            AllowWatchingReplay = false;
        }

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        /// <summary>
        /// The base results screen fetches the beatmap's online leaderboard to compute the player's
        /// rank and position. That request fails with <c>NotLoggedIn</c> whenever no osu! user token
        /// was supplied, which only produces a confusing log line in a recorded video's tail.
        /// Skip the fetch in that case; when a token *is* configured, keep the real online behaviour
        /// so the results screen can show the player's global rank.
        /// </summary>
        protected override Task<ScoreInfo[]> FetchScores()
            => api.IsLoggedIn ? base.FetchScores() : Task.FromResult(Array.Empty<ScoreInfo>());

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Disable the score panel "flair" (circle fill + counters counting up from zero) before
            // the panels finish their async load, so the recorded results screen is fully-formed.
            foreach (var panel in ScorePanelList.GetScorePanels())
            {
                FieldInfo? field = typeof(ScorePanel).GetField("displayWithFlair", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(panel, false);
            }

            // Hide the bottom toolbar before the screen is ever drawn.
            hideBottomToolbar();
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);

            ScorePanel? mainPanel = ScorePanelList.GetScorePanels().FirstOrDefault();
            if (mainPanel != null)
            {
                mainPanel.State = PanelState.Expanded;

                // Instantly complete the expand transition so the card is final-sized from the first
                // visible frame.
                mainPanel.FinishTransforms(true);
            }

            StatisticsPanel.Show();

            // Re-hide after the enter transition, in case any toolbar buttons are created or re-shown
            // by a later async load.
            Scheduler.AddDelayed(hideBottomToolbar, 1200);
        }

        /// <summary>
        /// The results screen renders a bottom toolbar (collection / favourite / replay-download
        /// buttons). It has no place in a recorded video, so the whole bottom grid row is hidden.
        /// </summary>
        private void hideBottomToolbar()
        {
            try
            {
                foreach (Drawable d in descendants(this))
                {
                    if (d is CollectionButton or FavouriteButton or ReplayDownloadButton)
                        d.Alpha = 0;
                }

                GridContainer? grid = descendants(this).OfType<GridContainer>().FirstOrDefault();

                if (grid != null)
                {
                    var cells = children(grid).ToList();

                    if (cells.Count >= 2)
                        cells[^1].Alpha = 0;
                }
            }
            catch
            {
                // Never fail the render because of a hidden toolbar.
            }
        }

        private static IEnumerable<Drawable> children(Drawable drawable)
        {
            if (drawable is not CompositeDrawable composite)
                return Array.Empty<Drawable>();

            var property = typeof(CompositeDrawable).GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            return ((property?.GetValue(composite) as IEnumerable)?.OfType<Drawable>() ?? Array.Empty<Drawable>()).ToList();
        }

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
    }
}

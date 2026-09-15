// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Skinning;

namespace LazerRender
{
    /// <summary>
    /// A <see cref="ReplayPlayer"/> subclass which replays a local .osr and records frames.
    ///
    /// The recorder re-sources the gameplay clock to its own <see cref="AdjustableManualClock"/> and
    /// advances it frame-by-frame, avoiding the seek/rewind path that suppresses hitsound playback.
    /// Lazer's <c>FrameStabilityContainer</c> (via <c>DrawableRuleset.FrameStablePlayback</c>) still
    /// steps the simulation deterministically at ~60Hz, so the draw rate can be scaled independently.
    ///
    /// Video is captured from a persistent FBO via <see cref="CaptureContainer"/>. The beatmap track
    /// is decoded offline through <see cref="BassTrackDecoder"/>, hitsounds are intercepted through
    /// <see cref="HitsoundMixer"/>, and both streams are muxed by <see cref="FfmpegFrameSink"/>.
    /// </summary>
    public partial class ReplayRecorderPlayer : ReplayPlayer
    {
        private const int audioSampleRate = 44100;
        private const int audioChannels = 2;

        /// <summary>Extra seconds of results screen recorded after the replay completes.</summary>
        private const double resultsTailSeconds = 5.0;

        /// <summary>
        /// Point inside the results tail (in seconds) at which the recorded video and audio start
        /// fading to black, so the results screen is held readable for a moment before the end.
        /// </summary>
        private const double resultsFadeStartSeconds = 3.5;

        /// <summary>Length of the results-tail fade (<see cref="resultsFadeStartSeconds"/> → 5.0s).</summary>
        private const double resultsFadeSeconds = 1.5;

        /// <summary>
        /// Duration of the fade-to-black recorded when the results screen is disabled. Deliberately
        /// 1.5× faster than the results-tail fade, i.e. the same 3.5s→5s style fade in ~1 second.
        /// </summary>
        private const double resultScreenFadeSeconds = 1.0;

        private enum RecordState
        {
            Idle,
            Recording,
            Done,
        }

        private readonly LazerRenderGameHost renderHost;
        private readonly CaptureContainer captureContainer;
        private readonly RecordOptions options;
        private readonly byte[]? trackAudio;
        private readonly double audioRate;
        private readonly bool trackAdjustsPitch;
        private readonly HitsoundMixer? hitsoundMixer;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        /// <summary>
        /// Drives the gameplay clock directly. Advancing <see cref="AdjustableManualClock.CurrentTime"/>
        /// moves gameplay forward frame-by-frame without seeking, which would otherwise mute hitsounds.
        /// </summary>
        private readonly AdjustableManualClock gameplayClock = new AdjustableManualClock { Rate = 1, IsRunning = true };

        private RecordState state = RecordState.Idle;
        private int frameIndex;
        private double recordingStartTime;

        /// <summary>
        /// Black overlay used to fade the recorded tail out. It covers the whole capture (including
        /// the results screen, which is a separate screen on top of the player).
        /// </summary>
        private Box? blackOverlay;

        private int? totalFrames => options.DurationSeconds.HasValue
            ? (int)Math.Ceiling(options.DurationSeconds.Value * options.Fps)
            : null;

        private int resultsTailFrames => (int)Math.Ceiling(resultsTailSeconds * options.Fps);
        private int blackFadeFrames => (int)Math.Ceiling(resultScreenFadeSeconds * options.Fps);

        /// <summary>Seconds recorded after the replay's final input frame.</summary>
        private double endTailSeconds => options.DisableResultScreen ? resultScreenFadeSeconds : resultsTailSeconds;

        /// <summary>Frames recorded after the replay's final input frame.</summary>
        private int endTailFrames => options.DisableResultScreen ? blackFadeFrames : resultsTailFrames;

        private double frameInterval => 1000.0 / options.Fps;

        /// <summary>Largest possible PCM byte count for one frame at the current fps.</summary>
        private int maxAudioBytesPerFrame => (int)Math.Ceiling(audioSampleRate / (double)options.Fps) * audioChannels * sizeof(short);

        public ReplayRecorderPlayer(Score score, LazerRenderGameHost renderHost, CaptureContainer captureContainer, RecordOptions options, byte[]? trackAudio, double audioRate, bool trackAdjustsPitch, HitsoundMixer? hitsoundMixer)
            : base(score)
        {
            this.renderHost = renderHost;
            this.captureContainer = captureContainer;
            this.options = options;
            this.trackAudio = trackAudio;
            this.audioRate = audioRate;
            this.trackAdjustsPitch = trackAdjustsPitch;
            this.hitsoundMixer = hitsoundMixer;

            // The recorder drives the results transition itself (see pushResultsScreen), so keep the
            // player's own auto-results flow disabled. Retries and input are never allowed.
            Configuration.ShowResults = false;
            Configuration.AllowRestart = false;
            Configuration.AllowUserInteraction = false;
            Configuration.AllowSkipping = false;
            Configuration.AllowPause = false;

            // ReplayPlayer enables the in-game leaderboard, but lazer's DrawableGameplayLeaderboard
            // hides its scores whenever this is false — independently of the HUD filter. So it may only
            // be enabled when the recorder was actually asked to keep the `scoreboard` HUD element;
            // otherwise the element would show up in a whitelist that excludes it.
            Configuration.ShowLeaderboard = !options.HudSpecified || options.HudComponents.Contains(HudVisibilityFilter.ScoreboardKey);

            Configuration.ShowFailingOverlay = false;
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);

            // Jump as close to the start of gameplay as possible.
            PerformIntroSkip(true);

            recordingStartTime = GameplayClockContainer.CurrentTime;

            // Replace the gameplay clock's source with our adjustable manual clock so time can be
            // advanced deterministically. Keeping the clock running (never calling Stop/Seek) avoids
            // the seek/rewind suppression that mutes hitsounds.
            gameplayClock.CurrentTime = recordingStartTime;
            attachGameplayClockToManualClock();

            frameIndex = 0;
            state = RecordState.Recording;

            _ = recordLoopAsync();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // The replay player draws a "Watching <player> play <map>" banner and a settings cog
            // above the gameplay. This has no place in a rendered video, so hide it permanently.
            ReplayOverlay.Hide();

            // Overlay used to fade the recorded tail out. Created up front at alpha 0 so that no
            // texture upload happens mid-fade; its alpha is driven per frame by the record loop.
            blackOverlay = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black,
                Alpha = 0,
                Depth = float.MinValue,
            };

            captureContainer.Add(blackOverlay);

            applyHudVisibilityDuringGameplay();
            applyAndWatchHudVisibility();
        }

        /// <summary>
        /// Makes the "hidden during gameplay" HUD visibility mode behave like it does for a human
        /// player. Lazer's <c>HUDOverlay</c> deliberately always shows the HUD for replays (so the
        /// seek bar stays visible), which makes the mode a no-op in a render; here we hide the HUD
        /// while the replay is actually playing instead.
        /// </summary>
        private void applyHudVisibilityDuringGameplay()
        {
            if (config.GetBindable<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode).Value != HUDVisibilityMode.HideDuringGameplay)
                return;

            if (HUDOverlay.ShowHud.Disabled)
                return; // autoplay-style renders already force the HUD off.

            HUDOverlay.ShowHud.Value = !LocalUserPlaying.Value;

            // Subscribed after HUDOverlay's own updateVisibility handler, so this wins and the HUD
            // stays hidden even when lazer's replay special-case re-shows it.
            LocalUserPlaying.BindValueChanged(playing =>
            {
                if (!HUDOverlay.ShowHud.Disabled)
                    HUDOverlay.ShowHud.Value = !playing.NewValue;
            });
        }

        /// <summary>
        /// Applies the requested HUD visibility immediately and again the moment the skin's HUD
        /// components finish their asynchronous load. The skin container is empty at
        /// <see cref="LoadComplete"/> time; <see cref="SkinnableContainer.OnComponentsLoaded"/> fires
        /// synchronously as the components are inserted, so the filter is in place before they are
        /// ever drawn (the previous single delayed pass let the opening seconds show the full HUD).
        /// </summary>
        private void applyAndWatchHudVisibility()
        {
            applyHudVisibilityFilter();

            if (!options.HudSpecified)
                return;

            foreach (SkinnableContainer container in HudVisibilityFilter.FindHudComponentContainers(HUDOverlay))
                container.OnComponentsLoaded += onHudComponentsLoaded;

            // Safety net: re-apply once the components have settled (a component may schedule its own
            // fade-in while loading). Recording is already running, so this does not affect timing.
            Scheduler.AddDelayed(applyHudVisibilityFilter, 1500);
        }

        private void onHudComponentsLoaded(Drawable _) => applyHudVisibilityFilter();

        /// <summary>
        /// Applies the requested HUD visibility: whitelist mode (hide everything except the listed
        /// components). When no whitelist was requested every component is shown.
        /// </summary>
        private void applyHudVisibilityFilter()
        {
            if (options.HudSpecified)
                HudVisibilityFilter.ApplyWhitelist(HUDOverlay, options.HudComponents);
        }

        /// <summary>
        /// Emits a single machine-readable JSON progress line to stdout for an external supervisor
        /// (the future web GUI) to consume. Fields: <c>type</c>, <c>phase</c>, <c>frame</c>,
        /// <c>total</c> (or <c>null</c> when unbounded) and <c>fps</c>.
        /// </summary>
        private void reportProgress(string phase, int frame, Stopwatch stopwatch)
        {
            double elapsed = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            double fps = frame / elapsed;

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = @"progress",
                phase,
                frame,
                total = reportTotalFrames,
                fps = Math.Round(fps, 1),
            }));
        }

        /// <summary>
        /// Total frames reported to the supervisor. With <c>--duration</c> this is the requested
        /// length; otherwise it is the natural replay length (final input frame + results tail) when
        /// that can be derived, so the web UI can show a determinate progress bar.
        /// </summary>
        private int? reportTotalFrames
        {
            get
            {
                if (options.DurationSeconds.HasValue)
                    return (int)Math.Ceiling(options.DurationSeconds.Value * options.Fps);

                double end = getReplayEndTime();
                if (double.IsNaN(end) || end <= 0)
                    return null;

                // recordingStartTime can legitimately be 0 (or a small negative lead-in).
                double seconds = (end - recordingStartTime) / (1000.0 * audioRate);
                if (seconds <= 0)
                    return null;

                return (int)Math.Ceiling(seconds * options.Fps) + endTailFrames;
            }
        }

        /// <summary>
        /// Points the gameplay clock at <see cref="gameplayClock"/>, and disables its interpolation
        /// drift so the clock tracks our manual values exactly even when recording faster than realtime.
        /// </summary>
        private void attachGameplayClockToManualClock()
        {
            FieldInfo? field = typeof(GameplayClockContainer).GetField("GameplayClock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field?.GetValue(GameplayClockContainer) is not FramedBeatmapClock clock)
            {
                Logger.Log(@"Hitsound clock: could not locate FramedBeatmapClock; gameplay clock will not be driven manually.");
                return;
            }

            clock.ChangeSource(gameplayClock);

            FieldInfo? interpField = typeof(FramedBeatmapClock).GetField("interpolatedTrack", BindingFlags.Instance | BindingFlags.NonPublic);

            if (interpField?.GetValue(clock) is InterpolatingFramedClock interp)
                interp.AllowableErrorMilliseconds = 0;
        }

        private async Task recordLoopAsync()
        {
            var sink = new FfmpegFrameSink(Path.Combine(options.OutputDirectory, "output.mp4"), options.Encoder);
            BassTrackDecoder? trackDecoder = null;

            try
            {
                // FFmpeg is started lazily by the first EnqueueFrame using the actual captured frame
                // dimensions, so the sink only needs the frame rate and target resolution up front.
                sink.Start(options.Fps, options.Width, options.Height, options.MotionBlurFrames);

                // Converted frames are pushed into the sink directly from the capture container's
                // background conversion thread; the recorder loop only paces the capture.
                captureContainer.StartReadback(sink.EnqueueFrame);

                // Let the game's initial texture upload burst (skin + beatmap background) drain before
                // the FBO capture starts. Reading the FBO while the GL upload queue is saturated has
                // caused native GPU driver crashes (flaky SIGSEGV) and, at 4K, stalled the first
                // frames. Poll the framework's internal upload-queue depth directly rather than
                // guessing a duration. The render clock is not advanced during this.
                await waitForTextureUploadsToSettleAsync();

                if (trackAudio is { Length: > 0 })
                {
                    string trackFile = Path.Combine(options.OutputDirectory, "track.tmp");
                    trackDecoder = BassTrackDecoder.Create(trackAudio, audioRate, trackAdjustsPitch, trackFile);
                }

                byte[] trackBuffer = new byte[maxAudioBytesPerFrame];
                byte[] hitsoundBuffer = new byte[maxAudioBytesPerFrame];

                // Fractional sample accumulator. 44100Hz / 120fps = 367.5 samples per channel, so
                // alternate between floor/ceil sample counts to keep perfect 44100Hz alignment.
                double audioRemainder = 0;

                // The replay is complete once the gameplay clock passes its final input frame.
                // From that point we keep recording the results screen for a short tail.
                double replayEndTime = getReplayEndTime();
                bool replayCompleted = false;
                int resultsStartFrame = 0;

                var stopwatch = Stopwatch.StartNew();
                reportProgress(@"PARSING", 0, stopwatch);

                while (state == RecordState.Recording && !RenderState.CancellationToken.IsCancellationRequested)
                {
                    // Advance the scene graph clock and the gameplay clock by one recorded frame.
                    double sceneTime = frameIndex * frameInterval;
                    double gameplayTime = recordingStartTime + frameIndex * frameInterval * audioRate;

                    // Detect the natural end of the replay and hand off to lazer's own results
                    // screen transition. This keeps the recorded timeline continuous instead of
                    // cutting the video at the last input frame.
                    if (!replayCompleted && replayEndTime > 0 && gameplayTime >= replayEndTime)
                    {
                        replayCompleted = true;
                        resultsStartFrame = frameIndex;

                        if (options.DisableResultScreen)
                            beginBlackFade();
                        else
                            pushResultsScreen();
                    }

                    // Fade the recorded tail out. A single shared ramp drives both the black overlay
                    // and the audio gain, so the video and the sound always fade together.
                    double tailFade = replayCompleted ? computeTailFade(frameIndex - resultsStartFrame) : 0.0;

                    if (replayCompleted && blackOverlay != null)
                        blackOverlay.Alpha = (float)tailFade;

                    renderHost.Clock.CurrentTime = sceneTime;
                    gameplayClock.CurrentTime = gameplayTime;

                    // Video: pace on the draw thread's render + async PBO download. The actual
                    // float→byte conversion and FFmpeg pipe write happen on background threads.
                    await captureContainer.CaptureAsync();


                    // Audio: compute this frame's exact sample count, carrying any fractional sample
                    // remainder forward so the output stays aligned with 44100Hz across all fps.
                    double desiredSamples = audioSampleRate / (double)options.Fps + audioRemainder;
                    int samplesPerChannel = (int)Math.Floor(desiredSamples);
                    audioRemainder = desiredSamples - samplesPerChannel;
                    int audioBytesThisFrame = samplesPerChannel * audioChannels * sizeof(short);

                    // The track's 0:00 strictly corresponds to gameplayTime == 0. Before that
                    // (lead-in) output silence. After the replay completes, keep playing the music
                    // (if the audio file has an unused tail) and fade it out with the same ramp the
                    // video uses.
                    if (trackDecoder != null && gameplayTime >= 0)
                    {
                        trackDecoder.Read(gameplayTime / (1000.0 * audioRate), trackBuffer, audioBytesThisFrame);

                        if (replayCompleted)
                            applyGain(trackBuffer, audioBytesThisFrame, 1.0 - tailFade);
                    }
                    else
                    {
                        Array.Clear(trackBuffer, 0, audioBytesThisFrame);
                    }

                    // Hitsounds: only mixed during gameplay. During the results tail we deliberately
                    // skip the sample mixdown so the results screen's applause/pop-in sounds do not
                    // pollute the recorded audio.
                    if (!replayCompleted && hitsoundMixer != null)
                    {
                        hitsoundMixer.Read(hitsoundBuffer, audioBytesThisFrame);
                        HitsoundMixer.AddClamped(trackBuffer, hitsoundBuffer, audioBytesThisFrame);
                    }

                    sink.WriteAudio(trackBuffer, audioBytesThisFrame);

                    frameIndex++;

                    if ((frameIndex % 60) == 0)
                        reportProgress(replayCompleted ? @"RESULTS_TAIL" : @"RENDERING_FRAMES", frameIndex, stopwatch);

                    // Stop once the results tail has been captured. During gameplay, the optional
                    // --duration is honoured, but if the replay's final frame lands just past that
                    // boundary (within the results-tail grace window) keep recording so the results
                    // transition is still captured instead of cutting the video mid-gameplay.
                    if (replayCompleted)
                    {
                        if (frameIndex - resultsStartFrame >= endTailFrames)
                            break;
                    }
                    else if (totalFrames is int durationFrames && frameIndex >= durationFrames)
                    {
                        double nextGameplayTime = recordingStartTime + frameIndex * frameInterval * audioRate;
                        double remainingGameplay = replayEndTime - nextGameplayTime;

                        if (replayEndTime <= 0 || remainingGameplay > endTailSeconds * 1000.0 * audioRate)
                            break;
                    }
                    else if (!totalFrames.HasValue && replayEndTime <= 0)
                    {
                        // Auto mode with no replay end signal (malformed replay): stop after a
                        // generous default instead of running forever.
                        if (frameIndex >= 60 * options.Fps)
                            break;
                    }
                }

                // Let the capture pipeline finish converting and enqueueing every frame before the
                // sink closes FFmpeg's stdin.
                await captureContainer.FlushAsync();

                reportProgress(@"FINALIZING", frameIndex, stopwatch);

                sink.Finish();

                state = RecordState.Done;
                Logger.Log($@"Recorded {frameIndex} frames to ""{Path.GetFullPath(options.OutputDirectory)}"".");
                reportProgress(@"DONE", frameIndex, stopwatch);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, @"Replay recording failed.");
            }
            finally
            {
                trackDecoder?.Dispose();
                sink.Dispose();
                renderHost.Host.Exit();
            }
        }

        /// <summary>
        /// Waits for the game's initial texture upload burst to drain by polling the framework's
        /// internal GL upload-queue depth, instead of guessing a fixed per-resolution delay. If the
        /// internal field cannot be located (it is framework-private), fall back to a short delay.
        /// </summary>
        private async Task waitForTextureUploadsToSettleAsync()
        {
            int? pending = getPendingTextureUploadCount();

            if (pending == null)
            {
                await Task.Delay(1000);
                return;
            }

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < 30000)
            {
                pending = getPendingTextureUploadCount();

                if (pending is <= 20)
                    return;

                await Task.Delay(100);
            }

            Logger.Log(@"Texture uploads did not drain within 30s; proceeding anyway.");
        }

        /// <summary>
        /// Returns the number of textures currently queued for GPU upload, read from the GL renderer's
        /// private <c>textureUploadQueue</c> (<see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>).
        /// </summary>
        private int? getPendingTextureUploadCount()
        {
            object renderer = renderHost.Host.Renderer;

            FieldInfo? field = renderer?.GetType().GetField(@"textureUploadQueue", BindingFlags.NonPublic | BindingFlags.Instance);
            object? queue = field?.GetValue(renderer);

            if (queue == null)
                return null;

            return queue.GetType().GetProperty(@"Count")?.GetValue(queue) as int?;
        }

        /// <summary>
        /// Applies a constant gain to an interleaved s16le stereo buffer, fading the map's remaining
        /// music out across the recorded tail. A gain of 1 is a no-op.
        /// </summary>
        private static void applyGain(byte[] buffer, int byteCount, double gain)
        {
            gain = Math.Clamp(gain, 0.0, 1.0);

            if (gain >= 1.0)
                return;

            for (int i = 0; i < byteCount; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                short faded = (short)Math.Round(sample * gain);
                buffer[i] = (byte)faded;
                buffer[i + 1] = (byte)(faded >> 8);
            }
        }

        /// <summary>
        /// Fade amount for a frame inside the recorded tail: 0 keeps the frame untouched, 1 renders it
        /// fully black and silent. When the results screen is disabled the fade starts immediately;
        /// otherwise it starts at <see cref="resultsFadeStartSeconds"/> so the results screen is held
        /// readable first.
        /// </summary>
        private double computeTailFade(int tailFrame)
        {
            double startSeconds = options.DisableResultScreen ? 0.0 : resultsFadeStartSeconds;
            double durationSeconds = options.DisableResultScreen ? resultScreenFadeSeconds : resultsFadeSeconds;

            if (durationSeconds <= 0)
                return 0.0;

            double elapsedSeconds = tailFrame * frameInterval / 1000.0;
            double progress = Math.Clamp((elapsedSeconds - startSeconds) / durationSeconds, 0.0, 1.0);

            // Smoothstep: gentle at both ends. An Easing.Out ramp reached near-black almost
            // immediately, which is what made the fade feel abrupt.
            return progress * progress * (3.0 - 2.0 * progress);
        }

        /// <summary>
        /// Returns the timestamp (in gameplay-clock milliseconds) of the final replay input frame.
        /// <c>NaN</c> means the replay data is empty, in which case completion is never signalled and
        /// the requested <c>--duration</c> is used as the only stop condition.
        /// </summary>
        private double getReplayEndTime()
        {
            if (Score.Replay?.Frames is { Count: > 0 } frames)
                return frames[^1].Time;

            return double.NaN;
        }

        /// <summary>
        /// Logs the replay→black transition when the results screen is disabled. The fade itself is
        /// applied per frame by the record loop (see <see cref="computeTailFade"/>), so the video and
        /// the audio ramp together instead of running on a framework transform.
        /// </summary>
        private void beginBlackFade()
            => Logger.Log(@"Replay complete; fading to black (results screen disabled).");

        /// <summary>
        /// Hands off to lazer's own results screen using the same factory the client uses after a
        /// replay, so the recorded output shows the native score panel + statistics transition.
        /// </summary>
        private void pushResultsScreen()
        {
            try
            {
                var results = CreateResults(Score.ScoreInfo);
                this.Push(results);
                Logger.Log(@"Replay complete; transitioning to the native results screen.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, @"Failed to show the results screen.");
            }
        }

        /// <summary>
        /// Uses the extended results screen so the statistics panel is already expanded when the
        /// score is shown (main card + Performance Breakdown / Timing Distribution / Accuracy
        /// Heatmap), matching the client's expanded view without requiring a click.
        /// </summary>
        protected override ResultsScreen CreateResults(ScoreInfo score)
            => new ExtendedResultsScreen(score);
    }

    /// <summary>
    /// An <see cref="IAdjustableClock"/> whose time is fully controlled by the recorder.
    /// <see cref="DecouplingFramedClock.ChangeSource"/> requires its source to implement
    /// <see cref="IAdjustableClock"/>, which <see cref="ManualClock"/> does not.
    /// </summary>
    internal sealed class AdjustableManualClock : IAdjustableClock
    {
        public double CurrentTime { get; set; }

        public double Rate { get; set; } = 1;

        public bool IsRunning { get; set; } = true;

        public bool Seek(double position)
        {
            CurrentTime = position;
            return true;
        }

        public void Reset()
        {
            CurrentTime = 0;
            IsRunning = false;
        }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void ResetSpeedAdjustments() => Rate = 1;
    }
}

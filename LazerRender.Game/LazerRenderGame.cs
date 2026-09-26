// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osuTK;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Leaderboards;
using osu.Game.Extensions;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Skinning;
using osu.Game.Users;

namespace LazerRender
{
    /// <summary>
    /// The recorder entry point. Builds on <see cref="OsuGameBase"/> so we inherit lazer's real
    /// database, ruleset, beatmap and skin infrastructure without pulling in the full menu/overlay UI.
    ///
    /// Depending on the selected <see cref="RunMode"/> this either imports an asset into the
    /// persistent Realm database, or renders a replay whose beatmap is resolved by MD5 hash.
    /// </summary>
    public partial class LazerRenderGame : OsuGameBase
    {
        private readonly LazerRenderGameHost renderHost;
        private readonly RecordOptions options;

        private OsuScreenStack screenStack = null!;
        private CaptureContainer captureContainer = null!;
        private HitsoundMixer? hitsoundMixer;

        public LazerRenderGame(LazerRenderGameHost renderHost, RecordOptions options)
        {
            this.renderHost = renderHost;
            this.options = options;

            Name = @"lazer-render";
        }

        /// <summary>
        /// Always talk to the production osu! services.
        ///
        /// lazer defaults to the development endpoints (<c>dev.ppy.sh</c>) for debug builds, and the
        /// recorder is normally run from source via <c>dotnet run</c> — i.e. a debug build. A user token
        /// issued by the production API is not valid there: <c>/me</c> answers 401 Unauthorized, lazer
        /// logs itself out, and online beatmap leaderboards silently stay empty. A recorder is never a
        /// test harness, so the development endpoints are never appropriate.
        /// </summary>
        public override bool UseDevelopmentServer => false;

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);

            // Drive the entire scene graph from the recorder's ManualClock. This makes every
            // animation (background fades, HUD, cursor trail, ...) advance deterministically
            // instead of following wall-clock time.
            Host.UpdateThread.Clock.ChangeSource(renderHost.Clock);

            // `OsuGameBase` creates `LocalConfig` here (in `SetHost`) and constructs its `APIAccess`
            // later, in its dependency loader, which reads `OsuSetting.Token` at construction and
            // validates it against `/me`. Injecting the token now therefore signs the recorder in via
            // the same persistent-login path the desktop client uses between sessions.
            applyOsuUserToken();
        }

        /// <summary>
        /// Applies recorder-specific framework defaults before the host creates its window.
        /// This is what gives us a fixed-size window, unlimited frame pacing and a silent audio device.
        /// </summary>
        protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults()
            => new Dictionary<FrameworkSetting, object>
            {
                { FrameworkSetting.WindowedSize, new Size(options.Width, options.Height) },
                { FrameworkSetting.WindowMode, WindowMode.Windowed },
                { FrameworkSetting.FrameSync, FrameSync.Unlimited },
                { FrameworkSetting.AudioDevice, @"No sound" },
                { FrameworkSetting.ShowLogOverlay, false },
                // Single-threaded execution makes update and draw strictly ordered, which is
                // required for deterministic, frame-accurate PNG capture.
                { FrameworkSetting.ExecutionMode, ExecutionMode.SingleThread },
            };

        /// <summary>
        /// Redirects all game storage (including <c>client.realm</c>) to the persistent server
        /// directory. This keeps the headless recorder away from any live osu!lazer install's
        /// database while still retaining imported beatmaps/skins between runs.
        /// </summary>
        protected override Storage CreateStorage(GameHost host, Storage defaultStorage)
            => host.GetStorage(Path.GetFullPath(options.StorageDirectory));

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Headless server: keep the physical OS window out of the way while the GL context still
            // drives the GPU and our FBO capture. Wayland may refuse to hide/minimise after mapping,
            // which is cosmetic; rendering must keep working regardless.
            try
            {
                Host.Window.Hide();
            }
            catch
            {
            }

            // Run update and draw as fast as the machine allows. The recorded timeline is driven
            // by our ManualClock, not by wall-clock pacing.
            Host.MaximumUpdateHz = 0;
            Host.MaximumDrawHz = 0;
            // The window is hidden, which osu.Framework treats as "inactive": the update/draw threads
            // then fall back to InactiveHz (default 60), throttling the whole draw loop to 60 fps.
            // Lift that too — this is the actual 60 fps ceiling, not the Wayland buffer swap.
            Host.MaximumInactiveHz = 0;

            switch (options.Mode)
            {
                case RunMode.ImportMap:
                    Scheduler.Add(() => _ = importBeatmapAsync());
                    return;

                case RunMode.ImportSkin:
                    Scheduler.Add(() => _ = importSkinAsync());
                    return;

                case RunMode.Purge:
                    Scheduler.Add(purgeAsync);
                    return;

                case RunMode.MapInfo:
                    Scheduler.Add(() => _ = mapInfoAsync());
                    return;

                case RunMode.ReplayInfo:
                    Scheduler.Add(() => _ = replayInfoAsync());
                    return;
            }

            // Create the hitsound capture hook before any beatmap/ruleset sample channels exist, so
            // the SampleMixer can be swapped to a decode mixer while it is still empty. This is the
            // earliest point where the AudioManager (and its mixers) are guaranteed to be available.
            hitsoundMixer = createHitsoundMixer();

            // Wrap the screen stack in an FBO-backed capture container so frames are read from a
            // deterministic, non-swapped framebuffer instead of the undefined post-swap backbuffer.
            captureContainer = new CaptureContainer
            {
                RelativeSizeAxes = Axes.Both,
                TargetWidth = options.Width,
                TargetHeight = options.Height,
                FlatFillMode = Environment.GetEnvironmentVariable(@"LAZERRENDER_FLATFILL") == @"1",
            };

            // DrawSizePreservingFillContainer lays every descendant out at the same virtual size the
            // real game uses (osu!lazer's OsuGame.ScalingContainerTargetDrawSize = 1024x768) and then
            // scales that layout to fill the output resolution. This is what gives the HUD its
            // resolution-dependent size (height/768 at 16:9) in-game; laying out directly at the
            // output size left the HUD at fixed native pixel sizes. An optional --hud-scale
            // multiplier (mirroring the in-game UI scale setting) shrinks the virtual layout so the
            // whole UI scales uniformly while the 4:3 playfield stays the same size.
            double uiScale = Math.Clamp(options.HudScale ?? 1.0, 0.5, 2.0);
            var preservingFill = new DrawSizePreservingFillContainer
            {
                RelativeSizeAxes = Axes.Both,
                TargetDrawSize = new Vector2((float)(1024.0 / uiScale), (float)(768.0 / uiScale)),
            };

            preservingFill.Add(screenStack = new OsuScreenStack { RelativeSizeAxes = Axes.Both });
            captureContainer.Add(preservingFill);
            Content.Add(captureContainer);

            Scheduler.Add(() => _ = beginPlaybackAsync());
        }

        private async Task mapInfoAsync()
        {
            try
            {
                // Yield so async game components (notably the BeatmapManager) finish loading
                // before the Realm query runs.
                await Task.Yield();

                BeatmapInfo? beatmap = BeatmapManager.QueryBeatmap(b => b.MD5Hash == options.MapInfoHash);

                // The service resolves metadata at queue time; downloading the map here means the
                // job card can show the real title before the render starts.
                if (beatmap == null && options.DownloadMissing)
                {
                    Logger.Log($@"Beatmap hash {options.MapInfoHash} not found locally; downloading via --download-missing.");

                    string oszPath = await downloadBeatmapAsync(options.MapInfoHash);

                    try
                    {
                        await BeatmapManager.Import(new ImportTask(oszPath));
                    }
                    finally
                    {
                        if (File.Exists(oszPath))
                            File.Delete(oszPath);
                    }

                    beatmap = BeatmapManager.QueryBeatmap(b => b.MD5Hash == options.MapInfoHash);
                }

                if (beatmap == null)
                {
                    Console.WriteLine(@"{""found"":false}");
                }
                else
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        found = true,
                        title = beatmap.Metadata.Title,
                        artist = beatmap.Metadata.Artist,
                        creator = beatmap.Metadata.Author?.Username ?? "",
                        version = beatmap.DifficultyName,
                        stars = beatmap.StarRating,
                    }));
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Map info lookup failed.");
                Console.WriteLine(@"{""found"":false}");
            }
            finally
            {
                Host.Exit();
            }
        }

        /// <summary>
        /// Decodes a replay and prints its render metadata as JSON: beatmap hash, the natural video
        /// duration (including the results tail) and the mod speed multiplier. Used by the web
        /// service to estimate output size before a render starts.
        /// </summary>
        private async Task replayInfoAsync()
        {
            try
            {
                await Task.Yield();

                string beatmapHash = readBeatmapHash(options.ReplayInfoPath);

                if (BeatmapManager.QueryBeatmap(b => b.MD5Hash == beatmapHash) == null && options.DownloadMissing)
                {
                    Logger.Log($@"Beatmap hash {beatmapHash} not found locally; downloading via --download-missing.");

                    string oszPath = await downloadBeatmapAsync(beatmapHash);

                    try
                    {
                        await BeatmapManager.Import(new ImportTask(oszPath));
                    }
                    finally
                    {
                        if (File.Exists(oszPath))
                            File.Delete(oszPath);
                    }
                }

                if (BeatmapManager.QueryBeatmap(b => b.MD5Hash == beatmapHash) == null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { found = false, beatmapMd5 = beatmapHash }));
                    return;
                }

                Score score = new DatabaseLegacyScoreDecoder(RulesetStore, BeatmapManager)
                    .Parse(File.OpenRead(options.ReplayInfoPath));

                BeatmapInfo? beatmapInfo = score.ScoreInfo.BeatmapInfo;
                WorkingBeatmap? working = beatmapInfo != null ? BeatmapManager.GetWorkingBeatmap(beatmapInfo) : null;

                double rate = score.ScoreInfo.Mods.OfType<ModRateAdjust>().FirstOrDefault()?.SpeedChange.Value ?? 1.0;

                double durationSeconds = 0;

                if (score.Replay?.Frames is { Count: > 0 } frames)
                {
                    double lastFrameTime = frames[^1].Time;
                    double firstObjectTime = working?.Beatmap.HitObjects.FirstOrDefault()?.StartTime ?? 0;
                    double recordingStartTime = firstObjectTime - 3000;
                    durationSeconds = Math.Max(0, (lastFrameTime - recordingStartTime) / (1000.0 * rate)) + 5.0;
                }

                double songLengthSeconds = working != null
                    ? Math.Round(working.Beatmap.CalculatePlayableLength() / 1000.0, 2)
                    : 0;

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    found = true,
                    beatmapMd5 = beatmapHash,
                    durationSeconds = Math.Round(durationSeconds, 2),
                    rate = Math.Round(rate, 3),
                    title = beatmapInfo?.Metadata.Title ?? "",
                    artist = beatmapInfo?.Metadata.Artist ?? "",
                    creator = beatmapInfo?.Metadata.Author?.Username ?? "",
                    version = beatmapInfo?.DifficultyName ?? "",
                    stars = beatmapInfo?.StarRating ?? 0,
                    songLengthSeconds,
                    mods = string.Concat(score.ScoreInfo.Mods.Select(m => m.Acronym)),
                    accuracy = Math.Round(score.ScoreInfo.Accuracy, 6),
                }));
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Replay info lookup failed.");
                Console.WriteLine(@"{""found"":false}");
            }
            finally
            {
                Host.Exit();
            }
        }

        private async Task importBeatmapAsync()
        {
            try
            {
                Live<BeatmapSetInfo>? imported = await BeatmapManager.Import(new ImportTask(options.ImportMapPath));

                if (imported == null)
                    throw new InvalidOperationException(@"Beatmap import returned no result.");

                imported.PerformRead(s =>
                    Console.WriteLine($@"Imported beatmap set ""{s.Metadata.Title} - {s.Metadata.Artist}"" ({s.Beatmaps.Count} beatmap(s))."));
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Beatmap import failed.");
            }
            finally
            {
                Host.Exit();
            }
        }

        private async Task importSkinAsync()
        {
            try
            {
                Live<SkinInfo>? imported = await SkinManager.Import(new ImportTask(options.ImportSkinPath));

                if (imported == null)
                    throw new InvalidOperationException(@"Skin import returned no result.");

                imported.PerformRead(s =>
                {
                    Console.WriteLine($@"Imported skin ""{s.Name}"".");

                    // Machine-readable counterpart so the web service can store the engine's canonical
                    // skin name (the skin.ini Name, which can differ from the archive/file name) instead
                    // of guessing it from the upload's filename.
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        type = @"import",
                        kind = @"skin",
                        name = s.Name,
                        creator = s.Creator,
                    }));
                });
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Skin import failed.");
            }
            finally
            {
                Host.Exit();
            }
        }

        /// <summary>
        /// Deletes imported beatmaps and/or skins from the persistent Realm database, reclaiming the
        /// disk space their managed file storage occupies. This is the Phase 4 cleanup mechanism.
        /// </summary>
        private void purgeAsync()
        {
            try
            {
                int beatmapSetsDeleted = 0;
                int skinsDeleted = 0;

                if (options.PurgeTarget is @"beatmaps" or @"all")
                {
                    foreach (BeatmapSetInfo set in BeatmapManager.GetAllUsableBeatmapSets().ToArray())
                    {
                        BeatmapManager.Delete(set);
                        beatmapSetsDeleted++;
                    }
                }

                if (options.PurgeTarget is @"skins" or @"all")
                {
                    int skinCount = SkinManager.GetAllUsableSkins().Count();

                    if (skinCount > 0)
                    {
                        SkinManager.Delete(_ => true);
                        skinsDeleted = skinCount;
                    }
                }

                Console.WriteLine($@"Purged {beatmapSetsDeleted} beatmap set(s) and {skinsDeleted} skin(s).");
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Purge failed.");
            }
            finally
            {
                Host.Exit();
            }
        }

        private async Task beginPlaybackAsync()
        {
            try
            {
                // Yield to the scheduler so async game components (notably the ruleset config cache)
                // finish loading before we decode and push the player. The previous implementation got
                // this yielding for free from awaiting the beatmap import; the record path no longer
                // imports, so we yield explicitly.
                await Task.Yield();

                applyVisualToggles();

                // Resolve the replay's beatmap hash up front so a missing beatmap can be downloaded
                // before decoding. The decoder throws when the hash is absent, so the previous
                // "--download-missing" branch (which ran after decoding) was never reached.
                string beatmapHash = readBeatmapHash(options.ReplayPath);

                if (BeatmapManager.QueryBeatmap(b => b.MD5Hash == beatmapHash) == null && options.DownloadMissing)
                {
                    Logger.Log($@"Beatmap hash {beatmapHash} not found locally; downloading via --download-missing.");

                    string oszPath = await downloadBeatmapAsync(beatmapHash);

                    try
                    {
                        await BeatmapManager.Import(new ImportTask(oszPath));
                    }
                    finally
                    {
                        // The .osz is only needed for the import above; remove it so repeated renders
                        // of unknown replays don't accumulate temp files.
                        if (File.Exists(oszPath))
                            File.Delete(oszPath);
                    }
                }

                // Decode the .osr against the beatmap stored in our own Realm database, resolved by
                // the MD5 hash embedded in the replay header. No --beatmap argument is required.
                Score score = new DatabaseLegacyScoreDecoder(RulesetStore, BeatmapManager)
                    .Parse(File.OpenRead(options.ReplayPath));

                BeatmapInfo beatmapInfo = score.ScoreInfo.BeatmapInfo
                    ?? throw new InvalidOperationException(@"Replay did not resolve to a beatmap.");

                // lazer's leaderboard manager refuses to fetch online scores unless the beatmap is
                // known to be past the pending state. Maps imported from a bare .osu/.osz carry no
                // online status (`None`), so mark the in-memory instance as ranked. The server stays
                // the authority on which scores actually exist; this only satisfies the client guard.
                if (beatmapInfo.OnlineID > 0 && beatmapInfo.Status < BeatmapOnlineStatus.Pending)
                {
                    Logger.Log($@"Beatmap {beatmapInfo.OnlineID} has local status {beatmapInfo.Status}; treating it as ranked for online score lookups.");
                    beatmapInfo.Status = BeatmapOnlineStatus.Ranked;
                }

                // Best-effort avatar lookup: populate the score's user avatar URL before the results
                // screen loads. Failures (no key, no network, unknown user) fall back to the default
                // placeholder silently.
                await applyAvatarAsync(score);

                // Warm the online beatmap leaderboard before the player is pushed. The recorder
                // bypasses the song-select/player-loader flow that normally triggers this fetch, and
                // the gameplay scoreboard provider reads the result exactly once as it loads.
                await warmLeaderboardAsync(beatmapInfo, score.ScoreInfo.Ruleset);

                WorkingBeatmap working = BeatmapManager.GetWorkingBeatmap(beatmapInfo);

                // The gameplay clock container accesses the track synchronously when the player is
                // pushed; ensure it is loaded beforehand (normally the song-select -> player flow does
                // this, but the headless recorder pushes the player directly).
                working.LoadTrack();

                // Apply a custom skin if one was requested. Leaving it unset lets lazer fall back to
                // the default skin or the skin bundled inside the beatmap.
                applyRequestedSkin();

                byte[]? trackAudio = readTrackAudio(beatmapInfo);

                // The speed change drives both the gameplay clock and the offline track decode. Only
                // DT/HT expose the "adjust pitch" switch; Daycore/Nightcore always preserve pitch.
                ModRateAdjust? rateMod = score.ScoreInfo.Mods.OfType<ModRateAdjust>().FirstOrDefault();
                double audioRate = rateMod?.SpeedChange.Value ?? 1.0;
                bool trackAdjustsPitch = rateMod is ModDoubleTime { AdjustPitch.Value: true } or ModHalfTime { AdjustPitch.Value: true };

                // The player leases copies of these bindables when it is pushed; set the source
                // values first so the lease captures the correct beatmap/ruleset/mods.
                Beatmap.Value = working;
                Ruleset.Value = score.ScoreInfo.Ruleset;
                SelectedMods.Value = score.ScoreInfo.Mods;

                screenStack.PushSynchronously(new ReplayRecorderPlayer(score, renderHost, captureContainer, options, trackAudio, audioRate, trackAdjustsPitch, hitsoundMixer));
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Failed to start replay playback.");
                Host.Exit();
            }
        }

        /// <summary>
        /// Reads the beatmap MD5 hash from a lazer-format <c>.osr</c> header. The header begins with
        /// two LEB128 varints (ruleset id and format version), followed by the beatmap hash encoded
        /// as a <c>0x0b</c> string marker, a LEB128 length and the raw ASCII hash bytes.
        /// </summary>
        private static string readBeatmapHash(string osrPath)
        {
            using Stream stream = File.OpenRead(osrPath);

            readVarint(stream); // ruleset id
            readVarint(stream); // format version

            if (stream.ReadByte() != 0x0b)
                throw new InvalidOperationException(@"Unexpected .osr header: missing string marker.");

            int length = readVarint(stream);

            // Mirror the service's parser: a lazer beatmap hash is 32 ASCII characters, so a longer
            // length means the file is not a replay header. Bounds the allocation before it happens,
            // since this CLI also runs standalone against an untrusted file.
            if (length is < 0 or > 256)
                throw new InvalidOperationException(@"Unexpected .osr header: implausible hash length.");

            byte[] hash = new byte[length];
            stream.ReadExactly(hash);

            return System.Text.Encoding.ASCII.GetString(hash);
        }

        /// <summary>Reads an unsigned LEB128 varint from <paramref name="stream"/>.</summary>
        private static int readVarint(Stream stream)
        {
            int result = 0;
            int shift = 0;

            while (shift < 32)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    throw new EndOfStreamException(@"Unexpected end of stream while reading varint.");

                result |= (b & 0x7f) << shift;

                if ((b & 0x80) == 0)
                    return result;

                shift += 7;
            }

            throw new InvalidOperationException(@"Varint too long.");
        }

        /// <summary>
        /// Downloads a missing beatmap package from a public mirror (osu.direct, falling back to
        /// catboy.best) and returns the path to the downloaded <c>.osz</c>.
        /// </summary>
        private static async Task<string> downloadBeatmapAsync(string hash)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // catboy.best (and some Cloudflare-fronted mirrors) reject requests without a
            // User-Agent; osu.direct tolerates the default. Set one explicitly so the fallback works.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(@"LazerRender/1.0");

            string tempPath = Path.Combine(Path.GetTempPath(), $@"lazerrender-{hash}.osz");

            // Primary: osu.direct. Its MD5 endpoint now returns JSON metadata rather than the .osz
            // bytes, so resolve the beatmapset id first and fetch the archive from the set endpoint.
            try
            {
                string metadata = await http.GetStringAsync($@"https://osu.direct/api/v2/md5/{hash}");

                using (JsonDocument doc = JsonDocument.Parse(metadata))
                {
                    if (doc.RootElement.TryGetProperty(@"beatmapset_id", out JsonElement setIdElement)
                        && setIdElement.TryGetInt32(out int setId)
                        && setId > 0)
                    {
                        byte[] data = await http.GetByteArrayAsync($@"https://osu.direct/d/{setId}");

                        if (data.Length > 100)
                        {
                            await File.WriteAllBytesAsync(tempPath, data);
                            return tempPath;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($@"osu.direct download failed for {hash}: {e.Message}");
            }

            // Fallback: catboy.best (independent mirror). Its MD5 endpoint returns the parent set id,
            // which is then fetched from the set-download endpoint. Nerinyan's search API is currently
            // unavailable (HTTP 530 / Cloudflare origin error 1033), so it is not used here.
            try
            {
                string metadata = await http.GetStringAsync($@"https://catboy.best/api/md5/{hash}");

                using (JsonDocument doc = JsonDocument.Parse(metadata))
                {
                    if (doc.RootElement.TryGetProperty(@"ParentSetID", out JsonElement setIdElement)
                        && setIdElement.TryGetInt32(out int setId)
                        && setId > 0)
                    {
                        byte[] data = await http.GetByteArrayAsync($@"https://catboy.best/d/{setId}");

                        if (data.Length > 100)
                        {
                            await File.WriteAllBytesAsync(tempPath, data);
                            return tempPath;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($@"catboy.best download failed for {hash}: {e.Message}");
            }

            throw new InvalidOperationException($@"Could not download beatmap {hash} from any mirror.");
        }

        /// <summary>
        /// Looks up the replay's player on the osu! API v2 and fills in their avatar on the decoded
        /// <see cref="Score"/>, so the results screen and the <c>avatar</c>/<c>flags</c> HUD elements
        /// show the real thing instead of the default placeholder. This is best-effort and never
        /// blocks a render on network failure.
        /// </summary>
        private async Task applyAvatarAsync(Score score)
        {
            if (score.ScoreInfo.User is not APIUser user || string.IsNullOrWhiteSpace(user.Username))
            {
                Logger.Log(@"Avatar: the replay score has no username; using default placeholder.");
                return;
            }

            // The legacy decoder only fills the username on the score's APIUser (whose id keeps its
            // default of 1) and stores the real osu! user id on `ScoreInfo.RealmUser` (lazer replays
            // embed a user_id). `DrawableAvatar` refuses to load a remote avatar unless
            // `user.OnlineID > 1`, so without this propagation every avatar silently resolves to the
            // bundled guest placeholder regardless of any avatar URL we set.
            long replayUserId = score.ScoreInfo.RealmUser.OnlineID;

            if (replayUserId > 1 && user.Id <= 1)
            {
                user.Id = (int)replayUserId;
                Logger.Log($@"Avatar: using the replay's user id {user.Id} for ""{user.Username}"".");
            }

            // A user token works for this public endpoint too; prefer it over the avatar-only key.
            string? token = options.OsuUserToken ?? options.AvatarApiKey;

            if (!string.IsNullOrWhiteSpace(token))
                await enrichAvatarFromApiAsync(user, token!);
            else
                Logger.Log(@"Avatar: no osu! API token provided; relying on the replay's user id.");

            if (user.OnlineID <= 1 && string.IsNullOrWhiteSpace(user.AvatarUrl))
            {
                Logger.Log($@"Avatar: no osu! user id or avatar URL for ""{user.Username}""; using default placeholder.");
                return;
            }

            preWarmAvatar(user);
        }

        /// <summary>
        /// Enriches a replay user with the canonical id, avatar URL and country code from
        /// <c>GET /api/v2/users/{user}</c>. The endpoint is public, so any token (user or
        /// client-credentials) is sufficient.
        /// </summary>
        private static async Task enrichAvatarFromApiAsync(APIUser user, string token)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                string url = $@"https://osu.ppy.sh/api/v2/users/{Uri.EscapeDataString(user.Username)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue(@"Bearer", token);

                Logger.Log($@"Avatar: GET {url}");

                using var response = await http.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();

                Logger.Log($@"Avatar: HTTP {(int)response.StatusCode} {response.StatusCode} ({(response.IsSuccessStatusCode ? "success" : "failure")}).");

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($@"Avatar: response body: {truncate(body)}");
                    return;
                }

                using var json = JsonDocument.Parse(body);

                if (json.RootElement.TryGetProperty(@"id", out JsonElement idElement) && idElement.TryGetInt32(out int apiUserId) && apiUserId > 1)
                    user.Id = apiUserId;

                if (json.RootElement.TryGetProperty(@"avatar_url", out JsonElement avatar)
                    && avatar.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(avatar.GetString()))
                {
                    user.AvatarUrl = avatar.GetString();
                }

                if (json.RootElement.TryGetProperty(@"country_code", out JsonElement country)
                    && country.ValueKind == JsonValueKind.String
                    && Enum.TryParse(country.GetString(), out CountryCode countryCode))
                {
                    user.CountryCode = countryCode;
                }

                Logger.Log($@"Avatar: resolved {user.Username} (id {user.OnlineID}, avatar {user.AvatarUrl ?? "(none)"}).");
            }
            catch (Exception ex)
            {
                Logger.Log($@"Avatar: lookup failed for ""{user.Username}"": {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads and uploads the avatar texture during the safe gameplay phase, through the very
        /// same store <c>DrawableAvatar</c> reads (<see cref="OnlineAssetCachingStore"/>).
        /// <see cref="OsuGameBase.Textures"/> is a different store, so warming through it does not
        /// stop the results screen from doing a remote fetch while already under heavy texture load
        /// at high resolutions — which is what used to crash the render.
        /// </summary>
        private void preWarmAvatar(APIUser user)
        {
            string? url = user.AvatarUrl;

            if (string.IsNullOrWhiteSpace(url) && user.OnlineID > 1)
                url = $@"https://a.ppy.sh/{user.OnlineID}";

            if (string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                Texture? texture = Dependencies.Get<OnlineAssetCachingStore>().Get(url);

                Logger.Log(texture != null
                    ? $@"Avatar: pre-warmed texture ""{url}"" for ""{user.Username}""."
                    : $@"Avatar: pre-warm returned no texture for ""{url}"".");
            }
            catch (Exception ex)
            {
                Logger.Log($@"Avatar: texture pre-warm failed for ""{url}"": {ex.Message}");
            }
        }

        /// <summary>
        /// Signs lazer's API provider in with <c>--osu-user-token</c> so online features (beatmap
        /// leaderboards) work. Must be a <em>user</em> token: lazer validates it against
        /// <c>/me</c>, which a client-credentials token cannot satisfy.
        /// </summary>
        private void applyOsuUserToken()
        {
            if (string.IsNullOrWhiteSpace(options.OsuUserToken))
            {
                Logger.Log(@"osu! API login: no --osu-user-token provided; online leaderboards are disabled.");
                return;
            }

            try
            {
                var token = new OAuthToken { AccessToken = options.OsuUserToken! };
                token.ExpiresIn = Math.Max(60, options.OsuUserTokenExpiresIn);

                // A one-shot render should not "remember" the token: clearing SavePassword makes
                // lazer's own token-changed handler blank the persisted value as soon as APIAccess
                // has consumed it. Set it before the token, because toggling SavePassword off resets
                // the stored token.
                LocalConfig.SetValue(OsuSetting.SavePassword, false);
                LocalConfig.SetValue(OsuSetting.Token, token.ToString());

                Logger.Log($@"osu! API login: injected user token (valid for {options.OsuUserTokenExpiresIn}s).");
            }
            catch (Exception ex)
            {
                Logger.Log($@"osu! API login: failed to inject the user token: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the injected token from lazer's persistent config on the way out, so a render host
        /// never leaves a live bearer token in the engine's ini file. This is best effort: SIGKILL
        /// cannot be handled, which is why the supervisor also passes the credential through a
        /// short-lived secrets file rather than the command line.
        /// </summary>
        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
                clearOsuUserToken();

            base.Dispose(isDisposing);
        }

        private void clearOsuUserToken()
        {
            if (string.IsNullOrWhiteSpace(options.OsuUserToken))
                return;

            try
            {
                LocalConfig.SetValue(OsuSetting.SavePassword, false);
                LocalConfig.SetValue(OsuSetting.Token, string.Empty);

                Logger.Log(@"osu! API login: cleared the injected user token from persistent config.");
            }
            catch (Exception ex)
            {
                Logger.Log($@"osu! API login: could not clear the injected user token: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether lazer's API provider considers itself logged in.
        /// </summary>
        private bool isApiLoggedIn()
        {
            try
            {
                return Dependencies.Get<IAPIProvider>().IsLoggedIn;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Waits (briefly) for <c>APIAccess</c> to finish its own sign-in round trip.
        /// </summary>
        private async Task<bool> waitForApiLoginAsync()
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (isApiLoggedIn())
                {
                    Logger.Log($@"osu! API login: connected after {stopwatch.ElapsedMilliseconds} ms.");
                    return true;
                }

                await Task.Delay(100);
            }

            return isApiLoggedIn();
        }

        /// <summary>
        /// Warms the online beatmap leaderboard before the player is pushed. The recorder bypasses
        /// the song-select and player-loader flow that normally calls
        /// <see cref="LeaderboardManager.FetchWithCriteria"/>, and the gameplay scoreboard provider
        /// reads <see cref="LeaderboardManager.Scores"/> exactly once when it loads — so without this
        /// the <c>scoreboard</c> HUD element would only ever show the replaying player.
        /// </summary>
        private async Task warmLeaderboardAsync(BeatmapInfo beatmapInfo, RulesetInfo ruleset)
        {
            if (string.IsNullOrWhiteSpace(options.OsuUserToken))
            {
                Logger.Log(@"Leaderboard: no osu! API login configured; the scoreboard will only show the replaying player.");
                return;
            }

            if (beatmapInfo.OnlineID <= 0)
            {
                Logger.Log($@"Leaderboard: the beatmap has no online id (id {beatmapInfo.OnlineID}, status {beatmapInfo.Status}); skipping the score fetch.");
                return;
            }

            // APIAccess signs in on its own thread, so give it a moment before giving up.
            if (!await waitForApiLoginAsync())
            {
                Logger.Log($@"Leaderboard: osu! API is not logged in (endpoint {describeApiEndpoint()}); the scoreboard will only show the replaying player. "
                           + @"The injected user token was rejected — it may be expired, revoked, or issued without the `public` scope, or the engine may be pointed at the development server rather than osu.ppy.sh.");
                return;
            }

            try
            {
                // Mirrors the scope a player would have selected in song select: lazer carries that
                // scope into the gameplay leaderboard via PlayerLoader, and the recorder has to set it
                // explicitly because it never goes through that flow.
                BeatmapLeaderboardScope scope = LeaderboardScopes.ToLazerScope(options.LeaderboardScope);
                string scopeKey = LeaderboardScopes.Normalise(options.LeaderboardScope);

                Logger.Log($@"Leaderboard: signed in as {describeSignedInUser()} via {describeApiEndpoint()}; fetching the {scopeKey} leaderboard for beatmap {beatmapInfo.OnlineID} (status {beatmapInfo.Status}).");

                var criteria = new LeaderboardCriteria(beatmapInfo, ruleset, scope, null);

                // FetchWithCriteria must run on the update thread.
                Scheduler.Add(() => LeaderboardManager.FetchWithCriteria(criteria, forceRefresh: true));

                var stopwatch = Stopwatch.StartNew();
                LeaderboardFailState? failState = null;

                while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
                {
                    // A benign cross-thread field read; the fetch itself completes on the API thread.
                    LeaderboardScores? scores = LeaderboardManager.Scores.Value;

                    if (scores != null)
                    {
                        // A rejection is final, not "still loading" — reporting it as a timeout hides the
                        // actual reason (auth, scope, beatmap availability), so surface it immediately.
                        if (scores.FailState is LeaderboardFailState state)
                        {
                            failState = state;
                            break;
                        }

                        Logger.Log($@"Leaderboard: fetched {scores.AllScores.Count()} {scopeKey} score(s) of {scores.TotalScores} for the scoreboard.");
                        return;
                    }

                    await Task.Delay(100);
                }

                if (failState != null)
                {
                    Logger.Log($@"Leaderboard: osu! rejected the {scopeKey} score fetch ({failState}) — {describeLeaderboardFailure(failState.Value)} The scoreboard will only show the replaying player.");
                    return;
                }

                Logger.Log(@"Leaderboard: score fetch did not complete in time; the scoreboard will only show the replaying player.");
            }
            catch (Exception ex)
            {
                Logger.Log($@"Leaderboard: score fetch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Describes the signed-in API identity, so a render's log shows which credential it actually
        /// used. Supportership matters for the scopes that require it.
        /// </summary>
        private string describeSignedInUser()
        {
            try
            {
                APIUser user = Dependencies.Get<IAPIProvider>().LocalUser.Value;
                return $@"""{user.Username}"" (id {user.OnlineID}{(user.IsSupporter ? @", supporter" : string.Empty)})";
            }
            catch
            {
                return @"(unknown user)";
            }
        }

        /// <summary>
        /// The osu! API root the engine is actually using. lazer switches to <c>dev.ppy.sh</c> for debug
        /// builds, where production tokens are rejected, so logging this pinpoints endpoint mismatches.
        /// </summary>
        private string describeApiEndpoint()
        {
            try
            {
                return Dependencies.Get<IAPIProvider>().Endpoints.APIUrl;
            }
            catch
            {
                return @"(unknown)";
            }
        }

        /// <summary>Maps a leaderboard failure state to an actionable explanation.</summary>
        private static string describeLeaderboardFailure(LeaderboardFailState state) => state switch
        {
            LeaderboardFailState.NotLoggedIn => @"the engine is not signed in.",
            LeaderboardFailState.BeatmapUnavailable => @"the beatmap has no usable online id or is not ranked yet.",
            LeaderboardFailState.NotSupporter => @"that scope requires osu!supporter on the signed-in account (use global).",
            LeaderboardFailState.NoTeam => @"the signed-in account is not on an osu! team (use global).",
            LeaderboardFailState.NetworkFailure => @"the API request failed (network error).",
            LeaderboardFailState.RulesetUnavailable => @"this ruleset has no online leaderboard.",
            LeaderboardFailState.NoneSelected => @"no beatmap or ruleset was selected.",
            _ => @"unknown reason.",
        };

        private static string truncate(string value, int maxLength = 800)
            => value.Length <= maxLength ? value : value[..maxLength] + @"…";

        /// <summary>
        /// Applies the per-render settings through osu!lazer's own config, before the player reads
        /// them while it loads. The <see cref="SettingsCatalog"/> table is the single source of
        /// truth; every value is set explicitly each run so persisted state from an earlier run
        /// cannot leak into the current render.
        /// </summary>
        private void applyVisualToggles()
        {
            SettingsEngine.Apply(LocalConfig, getOsuRulesetConfig(), options.Settings);
        }

        /// <summary>
        /// Retrieves the osu! ruleset's persistent config manager via the framework's
        /// <see cref="IRulesetConfigCache"/>. Returns <c>null</c> (ruleset settings skipped) if the
        /// cache is unavailable or the osu! ruleset does not expose a config manager.
        /// </summary>
        private OsuRulesetConfigManager? getOsuRulesetConfig()
        {
            try
            {
                var ruleset = RulesetStore.GetRuleset(0)?.CreateInstance();

                if (ruleset == null)
                    return null;

                return Dependencies.Get<IRulesetConfigCache>().GetConfigFor(ruleset) as OsuRulesetConfigManager;
            }
            catch (Exception ex)
            {
                Logger.Log($@"osu! ruleset config unavailable; ruleset settings will be skipped: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Applies the requested skin. When no skin is requested the built-in osu! "argon" pro skin
        /// is used (identical to argon apart from the absence of x300 hit popups).
        /// </summary>
        private void applyRequestedSkin()
        {
            string? requested = options.SkinName;

            if (string.IsNullOrWhiteSpace(requested))
            {
                applyDefaultSkin(@"No skin requested");
                return;
            }

            Live<SkinInfo>? skinInfo = SkinManager.GetAllUsableSkins()
                .FirstOrDefault(s => s.Value.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));

            // Legacy skins imported by lazer are stored as "<Name> [<archive>]"; also allow matching
            // by the bare name so users can pass the human-readable skin name.
            if (skinInfo == null)
            {
                skinInfo = SkinManager.GetAllUsableSkins()
                    .FirstOrDefault(s => s.Value.Name.StartsWith(requested + @" [", StringComparison.OrdinalIgnoreCase));
            }

            // A skin whose skin.ini supplied its own name is stored as "<skin.ini name> [<archive>]",
            // while the web service indexes skins by their archive (file) name. Match that trailing
            // archive tag too, otherwise such a skin is never found and the default skin is used.
            if (skinInfo == null)
            {
                skinInfo = SkinManager.GetAllUsableSkins()
                    .FirstOrDefault(s => s.Value.Name.EndsWith($@"[{requested}]", StringComparison.OrdinalIgnoreCase));
            }

            // A skin exported by lazer is stored under its skin.ini name, while its archive is named
            // "<name> (<creator>)" (that is <see cref="SkinInfo.ToString"/>). The service has
            // historically indexed skins by that archive/FILE name, so accept it here as well. This
            // also resolves rows imported before the service learned the engine's canonical name.
            if (skinInfo == null)
            {
                skinInfo = SkinManager.GetAllUsableSkins()
                    .FirstOrDefault(s => s.Value.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase));
            }

            if (skinInfo == null)
            {
                Logger.Log($@"Skin ""{requested}"" was not found; falling back to the default skin.");
                applyDefaultSkin($@"Skin ""{requested}"" not found");
                return;
            }

            SkinManager.CurrentSkinInfo.Value = skinInfo;
            Logger.Log($@"Applied skin ""{skinInfo.Value.Name}""");
        }

        /// <summary>
        /// Makes lazer's built-in osu! "argon" pro skin current. The pro variant is argon without the
        /// x300 hit popups, so it is the recorder's default skin.
        /// </summary>
        private void applyDefaultSkin(string reason)
        {
            Guid argonProId = ArgonProSkin.CreateInfo().ID;

            Live<SkinInfo>? skinInfo = SkinManager.GetAllUsableSkins()
                .FirstOrDefault(s => s.Value.ID == argonProId);

            if (skinInfo == null)
            {
                Logger.Log($@"{reason}: built-in argon pro skin not found; leaving the current skin untouched.");
                return;
            }

            SkinManager.CurrentSkinInfo.Value = skinInfo;
            Logger.Log($@"{reason}: applied default skin ""{skinInfo.Value.Name}""");
        }

        /// <summary>
        /// Reads the beatmap's audio track from the Realm file storage as raw bytes, so it can be
        /// decoded offline by <see cref="BassTrackDecoder"/>.
        /// </summary>
        private byte[]? readTrackAudio(BeatmapInfo beatmapInfo)
        {
            // The beatmap's MD5 was resolved from the Realm database, and detaching the model also
            // detaches its parent beatmap set (including Files), so the audio track can be located
            // without issuing a second Realm query.
            BeatmapSetInfo? set = beatmapInfo.BeatmapSet;

            if (set == null)
            {
                Logger.Log(@"Beatmap set not found in database; track audio will be unavailable.");
                return null;
            }

            var audioFile = set.Files.FirstOrDefault(f => f.Filename == beatmapInfo.Metadata.AudioFile)?.File;

            if (audioFile == null)
                return null;

            Storage fileStorage = Host.Storage.GetStorageForDirectory(@"files");

            using (Stream? stream = fileStorage.GetStream(audioFile.GetStoragePath()))
            using (var memory = new MemoryStream())
            {
                if (stream == null)
                    return null;

                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        /// <summary>
        /// Builds the reflection-based hitsound capture hook from the game's <see cref="AudioManager"/>
        /// and converts the sample mixer to an offline decode mixer before any samples are bound.
        /// </summary>
        private HitsoundMixer? createHitsoundMixer()
        {
            try
            {
                var mixer = new HitsoundMixer(Audio);
                mixer.ConvertToOfflineDecode();

                if (!mixer.IsAvailable)
                    Logger.Log(@"Hitsound mixer: sample mixer handle not ready yet; conversion will be retried during recording.");

                return mixer;
            }
            catch (Exception ex)
            {
                Logger.Log($@"Hitsound mixer initialisation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Decodes a legacy .osr by resolving the beatmap MD5 hash embedded in its header against
        /// the recorder's own Realm database, rather than a separately supplied beatmap file.
        /// </summary>
        private sealed class DatabaseLegacyScoreDecoder : LegacyScoreDecoder
        {
            private readonly IRulesetStore rulesets;
            private readonly BeatmapManager beatmaps;

            public DatabaseLegacyScoreDecoder(IRulesetStore rulesets, BeatmapManager beatmaps)
            {
                this.rulesets = rulesets;
                this.beatmaps = beatmaps;
            }

            protected override Ruleset GetRuleset(int rulesetId)
                => rulesets.GetRuleset(rulesetId)?.CreateInstance()
                   ?? throw new InvalidOperationException($@"No ruleset available for mode {rulesetId}.");

            protected override WorkingBeatmap GetBeatmap(string md5Hash)
            {
                BeatmapInfo? beatmap = beatmaps.QueryBeatmap(b => b.MD5Hash == md5Hash);

                if (beatmap == null)
                    throw new InvalidOperationException(
                        $@"Beatmap with hash {md5Hash} was not found in the local database. Import it first with --import-map.");

                return beatmaps.GetWorkingBeatmap(beatmap);
            }
        }
    }
}

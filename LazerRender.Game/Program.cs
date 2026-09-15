// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using osu.Game.Configuration;

namespace LazerRender
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            RenderState.CancellationToken = cts.Token;

            // Allow an external supervisor to abort a job cleanly. Ctrl+C and SIGTERM both cancel the
            // token; the recorder loop polls it and the FFmpeg sink's teardown kills its child process.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            using PosixSignalRegistration? sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => cts.Cancel());
            using PosixSignalRegistration? sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel());

            RecordOptions? options = parse(args);

            if (options == null)
            {
                printUsage();
                return 2;
            }

            // Rendering is intentionally limited to the supported 16:9 resolutions and refresh rates.
            // 240 fps (and arbitrary resolutions) are rejected up front: the pipeline has proven
            // fragile above 120 fps, and intermediate resolutions were never validated.
            string? limitError = validateRenderLimits(options);

            if (limitError != null)
            {
                Console.Error.WriteLine(limitError);
                return 2;
            }

            switch (options.Mode)
            {
                case RunMode.ImportMap:
                    if (!File.Exists(options.ImportMapPath))
                    {
                        Console.Error.WriteLine($@"Beatmap file not found: {options.ImportMapPath}");
                        return 2;
                    }

                    break;

                case RunMode.ImportSkin:
                    if (!File.Exists(options.ImportSkinPath))
                    {
                        Console.Error.WriteLine($@"Skin file not found: {options.ImportSkinPath}");
                        return 2;
                    }

                    break;

                case RunMode.MapInfo:
                    break;

                case RunMode.ReplayInfo:
                    if (!File.Exists(options.ReplayInfoPath))
                    {
                        Console.Error.WriteLine($@"Replay file not found: {options.ReplayInfoPath}");
                        return 2;
                    }

                    break;

                default:
                    if (!File.Exists(options.ReplayPath))
                    {
                        Console.Error.WriteLine($@"Replay file not found: {options.ReplayPath}");
                        return 2;
                    }

                    Directory.CreateDirectory(options.OutputDirectory);
                    break;
            }

            // The storage directory is persistent: it retains imported beatmaps, skins and the
            // client.realm database across runs so a headless server can build up a library over time.
            Directory.CreateDirectory(options.StorageDirectory);

            using var host = new LazerRenderGameHost();
            host.Run(new LazerRenderGame(host, options));

            return 0;
        }

        private static RecordOptions? parse(string[] args)
        {
            var options = new RecordOptions();
            bool hasCommand = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                string value(int next)
                {
                    if (next >= args.Length)
                        throw new ArgumentException($@"Missing value for {arg}");

                    return args[next];
                }

                switch (arg)
                {
                    case @"--import-map":
                        options.Mode = RunMode.ImportMap;
                        options.ImportMapPath = value(++i);
                        hasCommand = true;
                        break;

                    case @"--import-skin":
                        options.Mode = RunMode.ImportSkin;
                        options.ImportSkinPath = value(++i);
                        hasCommand = true;
                        break;

                    case @"--replay":
                        options.Mode = RunMode.Record;
                        options.ReplayPath = value(++i);
                        hasCommand = true;
                        break;

                    case @"--purge":
                        options.Mode = RunMode.Purge;
                        options.PurgeTarget = value(++i).ToLowerInvariant();
                        hasCommand = true;
                        break;

                    case @"--map-info":
                        options.Mode = RunMode.MapInfo;
                        options.MapInfoHash = value(++i).ToLowerInvariant();
                        hasCommand = true;
                        break;

                    case @"--replay-info":
                        options.Mode = RunMode.ReplayInfo;
                        options.ReplayInfoPath = value(++i);
                        hasCommand = true;
                        break;

                    case @"--skin":
                        options.SkinName = value(++i);
                        break;

                    case @"--output":
                        options.OutputDirectory = value(++i);
                        break;

                    case @"--storage":
                        options.StorageDirectory = value(++i);
                        break;

                    case @"--width":
                        options.Width = int.Parse(value(++i));
                        break;

                    case @"--height":
                        options.Height = int.Parse(value(++i));
                        break;

                    case @"--fps":
                        options.Fps = int.Parse(value(++i));
                        break;

                    case @"--duration":
                        options.DurationSeconds = double.Parse(value(++i));
                        break;

                    case @"--avatar-api-key":
                        options.AvatarApiKey = value(++i);
                        break;

                    case @"--osu-user-token":
                        options.OsuUserToken = value(++i);
                        break;

                    case @"--osu-user-token-expires-in":
                        options.OsuUserTokenExpiresIn = long.Parse(value(++i));
                        break;

                    case @"--motion-blur":
                        options.MotionBlurFrames = int.Parse(value(++i));
                        break;

                    case @"--download-missing":
                        options.DownloadMissing = true;
                        break;

                    case @"--hud-scale":
                        options.HudScale = double.Parse(value(++i));
                        break;

                    case @"--encoder":
                        options.Encoder = parseEncoder(value(++i));
                        break;

                    // Legacy visual toggles, kept for backward compatibility. They now feed the same
                    // settings table as the new generic flags below.
                    case @"--disable-storyboard":
                        options.Settings[@"storyboard"] = false;
                        break;

                    case @"--disable-video":
                        options.Settings[@"video"] = false;
                        break;

                    case @"--hide-overlay":
                        options.Settings[@"hudVisibility"] = HUDVisibilityMode.Never;
                        break;

                    // HUD whitelist: hide every HUD component except those listed. The flag accepts
                    // multiple space-separated (and/or comma-separated) keys and is repeatable.
                    case @"--hud":
                        options.HudSpecified = true;

                        while (i + 1 < args.Length && !args[i + 1].StartsWith(@"--", StringComparison.Ordinal))
                            addHudKeys(options, args[++i]);

                        break;

                    case @"--disable-result-screen":
                        options.DisableResultScreen = true;
                        break;

                    case @"--no-disable-result-screen":
                        options.DisableResultScreen = false;
                        break;

                    case @"--leaderboard-scope":
                        options.LeaderboardScope = parseLeaderboardScope(value(++i));
                        break;

                    case @"--render-config":
                        string configPath = value(++i);
                        string json = configPath == @"-" ? Console.In.ReadToEnd() : File.ReadAllText(configPath);
                        applyRenderConfig(options, json);
                        break;

                    case @"--help":
                    case @"-h":
                        return null;

                    default:
                        if (!tryParseSettingFlag(arg, args, ref i, options))
                        {
                            Console.Error.WriteLine($@"Unknown argument: {arg}");
                            return null;
                        }

                        break;
                }
            }

            if (!hasCommand)
            {
                Console.Error.WriteLine(@"Exactly one of --import-map, --import-skin, --replay, --purge, --map-info or --replay-info is required.");
                return null;
            }

            if (options.Mode == RunMode.Purge && options.PurgeTarget is not (@"beatmaps" or @"skins" or @"all"))
            {
                Console.Error.WriteLine($@"Unknown purge target: {options.PurgeTarget} (expected beatmaps, skins or all).");
                return null;
            }

            // The avatar API key may also be supplied through the environment.
            options.AvatarApiKey ??= Environment.GetEnvironmentVariable(@"OSU_API_KEY");

            return options;
        }

        /// <summary>
        /// Handles a generic settings-table flag: <c>--<flag></c> (bool true),
        /// <c>--no-<flag></c> (bool false) or <c>--<flag> <value></c> (numeric/enum).
        /// Returns <c>false</c> when <paramref name="arg"/> is not a known setting flag.
        /// </summary>
        private static bool tryParseSettingFlag(string arg, string[] args, ref int i, RecordOptions options)
        {
            if (!arg.StartsWith(@"--", StringComparison.Ordinal))
                return false;

            string body = arg[2..];

            bool negated = false;
            if (body.StartsWith(@"no-", StringComparison.Ordinal))
            {
                negated = true;
                body = body[3..];
            }

            SettingDescriptor? descriptor = SettingsCatalog.FindByFlag(body);
            if (descriptor == null)
                return false;

            if (descriptor.Kind == SettingValueKind.Bool)
            {
                if (negated)
                {
                    options.Settings[descriptor.Key] = false;
                    return true;
                }

                // A bare boolean flag enables the setting; an explicit value is also accepted.
                if (i + 1 < args.Length && !args[i + 1].StartsWith(@"--", StringComparison.Ordinal))
                {
                    string raw = args[++i];

                    if (!SettingsEngine.TryParseCli(descriptor, raw, out object value, out string? error))
                        throw new ArgumentException($@"Invalid value for --{descriptor.Flag}: {error}");

                    options.Settings[descriptor.Key] = value;
                }
                else
                {
                    options.Settings[descriptor.Key] = true;
                }

                return true;
            }

            if (negated)
                throw new ArgumentException($@"--no-{descriptor.Flag} is not valid for the non-boolean setting --{descriptor.Flag}.");

            if (i + 1 >= args.Length)
                throw new ArgumentException($@"Missing value for {arg}");

            string valueText = args[++i];

            if (!SettingsEngine.TryParseCli(descriptor, valueText, out object parsed, out string? parseError))
                throw new ArgumentException($@"Invalid value for --{descriptor.Flag}: {parseError}");

            options.Settings[descriptor.Key] = parsed;
            return true;
        }

        /// <summary>
        /// Applies a <c>--render-config</c> JSON document. Recognised keys are the settings-table
        /// keys, the <c>hud</c> whitelist and a small set of output-format keys.
        /// </summary>
        private static void applyRenderConfig(RecordOptions options, string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(@"--render-config JSON must be a single JSON object.");

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (SettingsCatalog.FindByKey(prop.Name) is SettingDescriptor descriptor)
                {
                    if (!SettingsEngine.TryParseJson(descriptor, prop.Value, out object value, out string? error))
                        throw new ArgumentException($@"Invalid value for ""{prop.Name}"": {error}");

                    options.Settings[descriptor.Key] = value;
                    continue;
                }

                if (prop.Name == @"hud")
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException(@"'hud' must be an array of HUD component keys.");

                    options.HudSpecified = true;

                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        string? key = item.GetString();

                        if (key == null || !HudVisibilityFilter.IsKnownKey(key))
                            throw new ArgumentException($@"Unknown HUD component ""{key}"" in ""hud"". {knownHudKeysHint}");

                        options.HudComponents.Add(key);
                    }

                    continue;
                }

                switch (prop.Name)
                {
                    case @"fps":
                        options.Fps = prop.Value.GetInt32();
                        break;

                    case @"width":
                        options.Width = prop.Value.GetInt32();
                        break;

                    case @"height":
                        options.Height = prop.Value.GetInt32();
                        break;

                    case @"motionBlur":
                        options.MotionBlurFrames = prop.Value.GetInt32();
                        break;

                    case @"hudScale":
                        options.HudScale = prop.Value.GetDouble();
                        break;

                    case @"disableResultScreen":
                        options.DisableResultScreen = prop.Value.GetBoolean();
                        break;

                    case @"leaderboardScope":
                        options.LeaderboardScope = parseLeaderboardScope(prop.Value.GetString());
                        break;

                    case @"skin":
                        options.SkinName = prop.Value.GetString();
                        break;

                    case @"duration":
                        options.DurationSeconds = prop.Value.GetDouble();
                        break;

                    default:
                        throw new ArgumentException($@"Unknown setting ""{prop.Name}"" in --render-config.");
                }
            }
        }

        /// <summary>
        /// Enforces the supported render matrix: 1280x720 / 1920x1080 / 2560x1440 / 3840x2160 at
        /// 30 / 60 / 90 / 120 fps. Returns an error message for anything outside that matrix, or
        /// null when the request is valid (non-record modes are always valid).
        /// </summary>
        private static string? validateRenderLimits(RecordOptions options)
        {
            if (options.Mode != RunMode.Record)
                return null;

            int[] allowedFps = { 30, 60, 90, 120 };
            (int width, int height)[] allowedResolutions =
            {
                (1280, 720),
                (1920, 1080),
                (2560, 1440),
                (3840, 2160),
            };

            if (Array.IndexOf(allowedFps, options.Fps) < 0)
                return $@"Unsupported frame rate {options.Fps}: supported values are 30, 60, 90 and 120 fps.";

            foreach ((int width, int height) in allowedResolutions)
            {
                if (options.Width == width && options.Height == height)
                    return null;
            }

            return $@"Unsupported resolution {options.Width}x{options.Height}: supported resolutions are 1280x720, 1920x1080, 2560x1440 and 3840x2160.";
        }

        private static string knownHudKeysHint => $@"Known components: {string.Join(@", ", HudVisibilityFilter.AllKeys)}.";

        /// <summary>
        /// Validates a leaderboard scope and normalises it to a known key.
        /// </summary>
        private static string parseLeaderboardScope(string? value)
        {
            if (value == null || !LeaderboardScopes.IsKnown(value))
                throw new ArgumentException($@"Unknown leaderboard scope ""{value}"". Known scopes: {string.Join(@", ", LeaderboardScopes.All)}.");

            return LeaderboardScopes.Normalise(value);
        }

        /// <summary>
        /// Adds one or more comma-separated HUD component keys to the whitelist, validating each.
        /// </summary>
        private static void addHudKeys(RecordOptions options, string value)
        {
            foreach (string key in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!HudVisibilityFilter.IsKnownKey(key))
                    throw new ArgumentException($@"Unknown HUD component ""{key}"" for --hud. {knownHudKeysHint}");

                options.HudComponents.Add(key);
            }
        }

        private static EncoderKind parseEncoder(string value) => value.ToLowerInvariant() switch
        {
            @"cpu" => EncoderKind.Cpu,
            @"amd" => EncoderKind.Amd,
            @"nvidia" => EncoderKind.Nvidia,
            @"intel" => EncoderKind.Intel,
            _ => throw new ArgumentException($@"Unknown encoder: {value} (expected cpu, amd, nvidia or intel)"),
        };

        private static void printUsage()
        {
            Console.WriteLine(
@"LazerRender - headless, faster-than-realtime replay recorder

Usage:
  LazerRender --replay <path.osr> [options]
  LazerRender --import-map <path.osu|osz>
  LazerRender --import-skin <path.osk>
  LazerRender --purge <beatmaps|skins|all>
  LazerRender --map-info <md5>
  LazerRender --replay-info <path.osr>

Commands (exactly one required):
  --replay <path.osr>       Render a replay. The beatmap is resolved from the
                            replay's MD5 hash against the persistent database.
  --import-map <path.osu|osz>  Import a beatmap package into the database.
  --import-skin <path.osk>     Import a legacy skin package into the database.
  --purge <beatmaps|skins|all> Delete imported beatmaps and/or skins to reclaim
                            server disk space.
  --map-info <md5>          Print beatmap metadata for an MD5 hash as JSON.
  --replay-info <path.osr>  Print replay + beatmap metadata as JSON.

Render options:
  --skin <name>     Apply a skin (by name) already present in the database.
                    Falls back to the built-in osu! ""argon"" pro skin if omitted or
                    unknown.
  --output <dir>    Output directory for the encoded video (default: frames)
  --storage <dir>   Persistent game storage directory (default: storage)
  --width <px>      Fixed window width (default: 1280)
  --height <px>     Fixed window height (default: 720)
  --fps <n>         Recorded draw rate (default: 60)
  --duration <sec>  Optional gameplay length; when omitted the render stops at
                    the replay end (plus the results tail)
  --download-missing  Download + import the beatmap from a public mirror when
                      the replay's MD5 hash is not in the local database
  --hud-scale <n>   Additional UI scale multiplier, mirroring the in-game UI
                    scale setting (default: 1.0 = lazer-native size)
  --disable-result-screen  Fade to black at the end of the replay and stop
                    instead of transitioning to the results screen
  --leaderboard-scope <scope>  Which beatmap leaderboard to warm for the scoreboard:
                    global (default), country, friend or team. Mirrors the scope
                    a player can pick in song select; needs a user token to have
                    any effect
  --avatar-api-key <key>  osu! API v2 client-credentials token for fetching the player
                    avatar (falls back to the OSU_API_KEY environment variable)
  --osu-user-token <token>  osu! API v2 *user* access token used to sign lazer in, so
                    online beatmap leaderboards / the scoreboard element work
  --osu-user-token-expires-in <sec>  Validity of --osu-user-token (default: 3600)
  --motion-blur <n>    Blend n frames with FFmpeg's tmix filter for motion blur
                        (default: 0 = disabled; 3 = light, 5 = heavy)
  --encoder <backend>  Video encoder backend: cpu (libx264), amd (VAAPI),
                        nvidia (NVENC) or intel (QSV) (default: cpu)
  --render-config <path>  JSON config document for per-render settings
                        (use '-' to read from stdin). See below for the keys.

HUD whitelist:
  --hud <keys...>   Space- and/or comma-separated (and repeatable) list of HUD
                    components to keep. Switches to whitelist mode: every HUD
                    component not listed is hidden. Omit entirely to show
                    everything; pass with no keys to hide the whole HUD.
                    Available components:
                      hp, combo, score, keyoverlay, accuracy, pp, hiterror,
                      song-progress, unstable-rate, judgements, mods, aim-error,
                      rank, longest-combo, scoreboard, bpm, cps, player-name,
                      avatar, flags, spectators, cosmetic");

            Console.WriteLine();
            Console.WriteLine(@"Per-render settings (also accepted as --render-config JSON keys;");
            Console.WriteLine(@"booleans accept --<flag> / --no-<flag>, numeric/enum accept --<flag> <value>):");

            foreach (SettingDescriptor d in SettingsCatalog.All)
            {
                string flagText = d.Kind == SettingValueKind.Bool
                    ? $@"--{d.Flag} / --no-{d.Flag}"
                    : $@"--{d.Flag} <value>";

                string? enumValues = d.EnumValues == null
                    ? null
                    : $@" [{string.Join("/", d.EnumValues.Keys)}]";

                Console.WriteLine($@"  {flagText,-42} {d.Description}{enumValues}  (key: ""{d.Key}"")");
            }
        }
    }
}

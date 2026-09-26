using System.Text.Json;
using LazerRender.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Estimates the size of a render output before it is started, so oversized requests can be rejected
/// before they fill the result disk.
///
/// The estimate is deliberately conservative: it models the video bitrate as a fixed number of bits
/// per pixel (covering CRF/QP-18 hardware and software encoders, including beatmaps with background
/// video), adds the AAC audio stream and a container overhead. The internal budget is calibrated to
/// one hour of 1080p60 footage by default, but is configurable via <c>Quota:MaxResultBytes</c>.
/// </summary>
public sealed class RenderSizeEstimator
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;
    private const int ReferenceFps = 60;
    private const int ReferenceDurationSeconds = 3600;

    private const double AudioBitrateBps = 192_000;

    private readonly QuotaOptions options;

    public RenderSizeEstimator(IOptions<QuotaOptions> options)
    {
        this.options = options.Value;
    }

    /// <summary>The internal result-size budget in bytes.</summary>
    public long LimitBytes => options.MaxResultBytes > 0
        ? options.MaxResultBytes
        : EstimateBytes(ReferenceWidth, ReferenceHeight, ReferenceFps, ReferenceDurationSeconds);

    /// <summary>Conservative estimate of the final <c>output.mp4</c> size in bytes.</summary>
    public long EstimateBytes(int width, int height, int fps, double durationSeconds)
    {
        double seconds = Math.Max(0, durationSeconds);
        double videoBytes = width * (double)height * fps * seconds * options.SizeBitsPerPixel / 8.0;
        double audioBytes = AudioBitrateBps * seconds / 8.0;
        return (long)Math.Ceiling((videoBytes + audioBytes) * options.SizeOverhead);
    }

    /// <summary>Parses the engine's <c>--replay-info</c> JSON stdout.</summary>
    public static ReplayRenderInfo? ParseReplayInfo(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
                continue;

            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.False)
                    return new ReplayRenderInfo(false, 0, 1, 0, null, null, 0);

                if (!found.GetBoolean())
                    continue;

                double duration = root.TryGetProperty("durationSeconds", out var d) && d.TryGetDouble(out var dv) ? dv : 0;
                double rate = root.TryGetProperty("rate", out var r) && r.TryGetDouble(out var rv) ? rv : 1;
                double songLength = root.TryGetProperty("songLengthSeconds", out var s) && s.TryGetDouble(out var sv) ? sv : 0;
                double stars = root.TryGetProperty("stars", out var st) && st.TryGetDouble(out var stv) ? stv : 0;
                double accuracy = root.TryGetProperty("accuracy", out var a) && a.TryGetDouble(out var av) ? av : 0;
                string? mods = root.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

                return new ReplayRenderInfo(
                    true, duration, rate, songLength, mods, accuracy, stars,
                    GetString(root, "title"), GetString(root, "artist"), GetString(root, "creator"), GetString(root, "version"));
            }
            catch (JsonException)
            {
                // Ignore non-JSON / malformed lines and keep scanning.
            }
        }

        return null;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed record ReplayRenderInfo(
    bool Found,
    double DurationSeconds,
    double Rate,
    double SongLengthSeconds,
    string? Mods,
    double? Accuracy,
    double Stars,
    string? Title = null,
    string? Artist = null,
    string? Creator = null,
    string? Version = null);

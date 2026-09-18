using System.Diagnostics;
using LazerRender.Api.Configuration;
using LazerRender.Contracts;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Resolves the FFmpeg encoder for new jobs. An explicit <c>Renderer:Encoder</c> value wins;
/// otherwise the resolver probes FFmpeg once and caches the result.
///
/// Deterministic preference order: AMD VAAPI → NVIDIA NVENC → Intel QSV → CPU (libx264).
/// A backend is only selected when a real FFmpeg null-encode probe succeeds, so the service
/// never claims a GPU encoder merely because a GPU device exists.
/// </summary>
public sealed class EncoderResolver
{
    private readonly RendererOptions options;
    private readonly ILogger<EncoderResolver> logger;
    private readonly Lazy<EncoderKind> detected;

    public EncoderResolver(IOptions<RendererOptions> options, ILogger<EncoderResolver> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        detected = new Lazy<EncoderKind>(Detect, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAuto =>
        string.IsNullOrWhiteSpace(options.Encoder) ||
        options.Encoder.Equals("auto", StringComparison.OrdinalIgnoreCase);

    public EncoderKind Resolve()
    {
        var configured = options.Encoder;
        if (!string.IsNullOrWhiteSpace(configured) &&
            !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Parse(configured);
        }

        return detected.Value;
    }

    private EncoderKind Detect()
    {
        var resolved = EncoderKind.Cpu;

        if (ProbeAmd())
            resolved = EncoderKind.Amd;
        else if (ProbeNvidia())
            resolved = EncoderKind.Nvidia;
        else if (ProbeIntel())
            resolved = EncoderKind.Intel;

        logger.LogInformation("Encoder auto-detection selected {Encoder}.", resolved);

        return resolved;
    }

    private bool ProbeAmd()
    {
        if (!File.Exists("/dev/dri/renderD128"))
            return false;

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-vaapi_device", "/dev/dri/renderD128",
            "-f", "lavfi", "-i", "color=black:s=128x128:d=1",
            "-frames:v", "10",
            "-vf", "format=nv12,hwupload",
            "-c:v", "h264_vaapi",
            "-f", "null", "-",
        };

        return Probe("amd", args);
    }

    private bool ProbeNvidia()
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=black:s=128x128:d=1",
            "-frames:v", "10",
            "-c:v", "h264_nvenc",
            "-f", "null", "-",
        };

        return Probe("nvidia", args);
    }

    private bool ProbeIntel()
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-init_hw_device", "qsv=hw",
            "-filter_hw_device", "hw",
            "-f", "lavfi", "-i", "color=black:s=128x128:d=1",
            "-frames:v", "10",
            "-vf", "format=nv12,hwupload=extra_hw_frames=64,format=qsv",
            "-c:v", "h264_qsv",
            "-f", "null", "-",
        };

        return Probe("intel", args);
    }

    private bool Probe(string kind, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            if (process.ExitCode == 0)
                return true;

            // The probe output is a few lines (ffmpeg runs at -loglevel error), so reading it after exit
            // cannot block; surfacing it is what makes a silent CPU fallback diagnosable.
            string error = process.StandardError.ReadToEnd().Trim();
            logger.LogDebug(
                "Encoder probe {Kind} failed with exit code {ExitCode}: {Error}",
                kind, process.ExitCode, error);

            return false;
        }
        catch (Exception e)
        {
            logger.LogWarning("Encoder probe {Kind} failed: {Message}", kind, e.Message);
            return false;
        }
    }

    private static EncoderKind Parse(string value) => value.ToLowerInvariant() switch
    {
        "amd" => EncoderKind.Amd,
        "nvidia" => EncoderKind.Nvidia,
        "intel" => EncoderKind.Intel,
        _ => EncoderKind.Cpu,
    };
}

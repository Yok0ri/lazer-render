using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LazerRender.Contracts;

namespace LazerRender.Api.Services;

/// <summary>
/// Collects the admin "Render PC" summary (Phase 8.3). Collection is deliberately decoupled from the
/// request path: <see cref="SystemInfoWarmupService"/> fills the cache once at startup and the endpoint
/// serves the cached value, so opening the panel can never race the render worker for a probe.
/// <see cref="GetAsync"/> with <c>refresh: true</c> recomputes on demand (the card's Refresh button).
///
/// Every probe is best-effort and isolated: a missing `/proc`, no `ffmpeg` on PATH or an unreadable
/// sysfs entry yields a null field, never an exception.
/// </summary>
public sealed class SystemInfoService
{
    private readonly EncoderResolver encoderResolver;
    private readonly StorageService storage;
    private readonly ILogger<SystemInfoService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    private volatile RenderPcDto? current;

    public SystemInfoService(
        EncoderResolver encoderResolver,
        StorageService storage,
        ILogger<SystemInfoService> logger)
    {
        this.encoderResolver = encoderResolver;
        this.storage = storage;
        this.logger = logger;
    }

    /// <summary>The last collected summary, or null before the first successful collection.</summary>
    public RenderPcDto? Current => current;

    public async Task<RenderPcDto> GetAsync(bool refresh, CancellationToken ct)
    {
        RenderPcDto? cached = current;
        if (!refresh && cached is not null)
            return cached;

        await gate.WaitAsync(ct);

        try
        {
            // Another caller may have refreshed while we waited.
            cached = current;
            if (!refresh && cached is not null)
                return cached;

            RenderPcDto collected = await Task.Run(Collect, ct);
            current = collected;
            return collected;
        }
        finally
        {
            gate.Release();
        }
    }

    private RenderPcDto Collect()
    {
        (string? gpu, string? gpuDriver) = ProbeGpu();

        long? freeBytes = null;
        long? totalBytes = null;

        try
        {
            string? root = Path.GetPathRoot(storage.ResultsDirectory);

            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);

                if (drive.IsReady)
                {
                    freeBytes = drive.AvailableFreeSpace;
                    totalBytes = drive.TotalSize;
                }
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not read free space for the results volume.");
        }

        EncoderKindLike encoder = ResolveEncoder();

        return new RenderPcDto(
            OperatingSystem: RuntimeInformation.OSDescription,
            DotnetRuntime: RuntimeInformation.FrameworkDescription,
            CpuModel: ProbeCpuModel(),
            CpuCores: Environment.ProcessorCount,
            MemoryTotalBytes: ProbeMemoryTotal(),
            Gpu: gpu,
            GpuDriver: gpuDriver,
            FfmpegVersion: ProbeFfmpeg(),
            Encoder: encoder.Name,
            EncoderAutoDetected: encoder.AutoDetected,
            ResultsPath: storage.ResultsDirectory,
            ResultsFreeBytes: freeBytes,
            ResultsTotalBytes: totalBytes,
            CollectedAt: DateTimeOffset.UtcNow);
    }

    private EncoderKindLike ResolveEncoder()
    {
        try
        {
            return new EncoderKindLike(
                encoderResolver.Resolve().ToString().ToLowerInvariant(),
                encoderResolver.IsAuto);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Encoder resolution failed while building the Render PC summary.");
            return new EncoderKindLike("unknown", encoderResolver.IsAuto);
        }
    }

    private static string ProbeCpuModel()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (!line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    continue;

                int colon = line.IndexOf(':');

                if (colon >= 0)
                    return line[(colon + 1)..].Trim();
            }
        }
        catch
        {
            // /proc/cpuinfo is Linux-only; fall through to the generic label.
        }

        return "unknown";
    }

    private static long? ProbeMemoryTotal()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    continue;

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 2
                    && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kilobytes))
                {
                    return kilobytes * 1024L;
                }
            }
        }
        catch
        {
            // /proc/meminfo is Linux-only.
        }

        return null;
    }

    private static (string? Gpu, string? Driver) ProbeGpu()
    {
        try
        {
            string[] cards = Directory.GetDirectories("/sys/class/drm", "card[0-9]*");

            foreach (string card in cards.OrderBy(c => c, StringComparer.Ordinal))
            {
                string device = Path.Combine(card, "device");

                if (!Directory.Exists(device))
                    continue;

                string? uevent = TryRead(Path.Combine(device, "uevent"));

                if (uevent is null)
                    continue;

                string? pci = MatchValue(uevent, "PCI_ID=");
                string? driver = MatchValue(uevent, "DRIVER=");

                if (pci is null && driver is null)
                    continue;

                string label = pci is null ? "GPU" : $"PCI {pci}";
                return (label, driver);
            }
        }
        catch
        {
            // /sys/class/drm is Linux-only.
        }

        return (null, null);
    }

    private static string? ProbeFfmpeg()
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

            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);

            if (process is null)
                return null;

            string? firstLine = process.StandardOutput.ReadLine();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
        }
        catch
        {
            // ffmpeg may be absent on a development machine.
            return null;
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? MatchValue(string text, string prefix)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return null;
    }

    private readonly record struct EncoderKindLike(string Name, bool AutoDetected);
}

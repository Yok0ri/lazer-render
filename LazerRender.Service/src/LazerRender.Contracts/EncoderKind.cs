namespace LazerRender.Contracts;

/// <summary>
/// FFmpeg video encoder backend. Mirrors LazerRender's <c>--encoder</c> flag exactly.
/// Serialized as lowercase strings: cpu, amd, nvidia, intel.
/// </summary>
public enum EncoderKind
{
    Cpu = 0,
    Amd = 1,
    Nvidia = 2,
    Intel = 3,
}

/// <summary>
/// User-facing backend names. The engine's <c>--encoder</c> value for <see cref="EncoderKind.Amd"/> is
/// <c>amd</c> for historical reasons, but the backend is VAAPI and is vendor-neutral — it drives Intel
/// iGPUs too — so the web UI must not label an Intel host as "AMD".
/// </summary>
public static class EncoderKindNames
{
    public static string Display(this EncoderKind kind) => kind switch
    {
        EncoderKind.Amd => "vaapi",
        EncoderKind.Nvidia => "nvenc",
        EncoderKind.Intel => "qsv",
        _ => "cpu",
    };
}

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

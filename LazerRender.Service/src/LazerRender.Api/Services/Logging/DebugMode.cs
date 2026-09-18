namespace LazerRender.Api.Services.Logging;

/// <summary>
/// The runtime half of the Phase 8.2 debug/release distinction. The compile-time half is the
/// <c>LAZERRENDER_DEBUG</c> constant defined for Debug builds in the repository-root
/// <c>Directory.Build.props</c>; it gates instrumentation that is compiled out of Release. This class
/// covers the paths that must be switchable without a rebuild, via the
/// <c>LAZERRENDER_DEBUG=1</c> environment variable.
/// </summary>
public static class DebugMode
{
    /// <summary>
    /// True when the process was built as Debug (compile-time constant) or the operator set
    /// <c>LAZERRENDER_DEBUG=1</c> at runtime. Used to lower ring-buffer capture levels to Debug and to
    /// enable verbose diagnostic paths.
    /// </summary>
    public static bool Enabled { get; } = Resolve();

    private static bool Resolve()
    {
        string? value = Environment.GetEnvironmentVariable("LAZERRENDER_DEBUG");

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

#if LAZERRENDER_DEBUG
        // A Debug build carries the instrumentation, so it defaults to verbose capture. Set
        // LAZERRENDER_DEBUG=0 to opt out of the extra ring-buffer volume while keeping the build.
        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
#else
        return false;
#endif
    }
}
namespace LazerRender.Api.Services;

/// <summary>
/// Owner-only filesystem permissions for credential material (the Data Protection key ring and the
/// per-render secrets file handed to the engine).
///
/// The service previously relied on the process umask, so <c>keys/</c> and its key files were created
/// <c>0755</c>/<c>0644</c> and were readable by every local account on the render host. Everything in
/// this class is owner-only by construction; the helpers are no-ops on Windows, where the Unix mode
/// bits do not apply.
/// </summary>
internal static class FilePermissions
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates <paramref name="path"/> if needed and restricts it to the owner (0700).</summary>
    public static void EnsureOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path, DirectoryMode);

        // An existing directory keeps whatever mode it already had, so always re-apply.
        File.SetUnixFileMode(path, DirectoryMode);
    }

    /// <summary>
    /// Tightens an existing directory and every key file already in it to owner-only. Key files are
    /// created by the framework after startup, but the 0700 directory already keeps other accounts out
    /// of those; this covers rings written by an earlier (less strict) version.
    /// </summary>
    public static void RestrictKeyRing(string path)
    {
        EnsureOwnerOnlyDirectory(path);

        if (OperatingSystem.IsWindows())
            return;

        foreach (string file in Directory.EnumerateFiles(path, "*.xml"))
        {
            try
            {
                File.SetUnixFileMode(file, OwnerOnlyFileMode);
            }
            catch (IOException)
            {
                // Best effort: a key file being rewritten concurrently is still inside the 0700 dir.
            }
        }
    }

    /// <summary>
    /// Opens a file for writing, created owner-readable only (0600) so there is no window in which the
    /// credential is world-readable.
    /// </summary>
    public static FileStream OpenOwnerOnlyFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnlyFileMode;

        return new FileStream(path, options);
    }
}

using LazerRender.Api.Services;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// H-3 (audit): the Data Protection key ring and the per-render secrets file are credential material.
/// They used to inherit the process umask (0755/0644 under a default umask 022), which made the key
/// ring readable by every other local account on the render host.
/// </summary>
public sealed class FilePermissionsTests
{
    private const UnixFileMode OwnerDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void Key_ring_directory_is_created_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        string dir = NewTempPath();

        try
        {
            FilePermissions.EnsureOwnerOnlyDirectory(dir);

            Assert.True(Directory.Exists(dir));
            Assert.Equal(OwnerDirectory, new DirectoryInfo(dir).UnixFileMode);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void An_existing_directory_is_tightened_not_left_alone()
    {
        if (OperatingSystem.IsWindows())
            return;

        string dir = NewTempPath();
        Directory.CreateDirectory(dir);

        try
        {
            // Simulate what the old code produced under a default umask.
            File.SetUnixFileMode(dir,
                OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            FilePermissions.EnsureOwnerOnlyDirectory(dir);

            Assert.Equal(OwnerDirectory, new DirectoryInfo(dir).UnixFileMode);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void Key_files_already_present_are_tightened()
    {
        if (OperatingSystem.IsWindows())
            return;

        string dir = NewTempPath();
        Directory.CreateDirectory(dir);
        string keyFile = Path.Combine(dir, "key-1.xml");
        File.WriteAllText(keyFile, "<key />");

        try
        {
            File.SetUnixFileMode(keyFile,
                OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            FilePermissions.RestrictKeyRing(dir);

            Assert.Equal(OwnerDirectory, new DirectoryInfo(dir).UnixFileMode);
            Assert.Equal(OwnerFile, new FileInfo(keyFile).UnixFileMode);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void Secrets_file_is_created_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        string dir = NewTempPath();
        Directory.CreateDirectory(dir);
        string secretFile = Path.Combine(dir, "secrets.json");

        try
        {
            using (FileStream stream = FilePermissions.OpenOwnerOnlyFile(secretFile))
            {
                stream.Write("{\"osuUserToken\":\"x\"}"u8);
            }

            // Created 0600 rather than chmodded afterwards, so there is no readable window.
            Assert.Equal(OwnerFile, new FileInfo(secretFile).UnixFileMode);
        }
        finally
        {
            Delete(dir);
        }
    }

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), "lazerrender-perms-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

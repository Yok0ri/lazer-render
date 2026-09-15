using System.IO.Compression;
using LazerRender.Api.Services;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// M-8 (audit): uploaded archives go straight to the engine's third-party parsers, which run in the
/// process holding the GPU context and the render credentials and had no explicit size or
/// decompression guard beyond the global 220 MB body limit.
/// </summary>
public sealed class ArchiveGuardTests
{
    [Fact]
    public void A_normal_package_is_accepted()
    {
        string dir = NewDir();

        try
        {
            string path = CreateZip(dir, archive =>
            {
                WriteEntry(archive, "readme.txt", "hello");
                WriteEntry(archive, "skin.ini", "[General]\nName: test\n");
            });

            Assert.Null(ArchiveGuard.Validate(path, new FileInfo(path).Length));
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void A_non_zip_upload_is_rejected()
    {
        string dir = NewDir();

        try
        {
            // A bare .osu beatmap file is not an archive; the guard must not throw on it.
            string path = Path.Combine(dir, "map.osu");
            File.WriteAllText(path, "osu file format v14\n");

            Assert.NotNull(ArchiveGuard.Validate(path, new FileInfo(path).Length));
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void Too_many_entries_are_rejected()
    {
        string dir = NewDir();

        try
        {
            string path = CreateZip(dir, archive =>
            {
                for (int i = 0; i <= ArchiveGuard.MaxEntries; i++)
                    WriteEntry(archive, $"file-{i}.txt", "");
            });

            string? error = ArchiveGuard.Validate(path, new FileInfo(path).Length);

            Assert.NotNull(error);
            Assert.Contains("entries", error);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void An_implausible_compression_ratio_is_rejected()
    {
        string dir = NewDir();

        try
        {
            // Highly compressible content: a small archive that would expand enormously.
            string path = CreateZip(dir, archive => WriteEntry(archive, "bomb.bin", new string('\0', 8 * 1024 * 1024)));

            string? error = ArchiveGuard.Validate(path, new FileInfo(path).Length);

            Assert.NotNull(error);
            Assert.Contains("compression ratio", error);
        }
        finally
        {
            Delete(dir);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string CreateZip(string dir, Action<ZipArchive> write)
    {
        string path = Path.Combine(dir, "package.osz");

        using (FileStream stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            write(archive);
        }

        return path;
    }

    private static string NewDir() =>
        Directory.CreateTempSubdirectory("lazerrender-archive-").FullName;

    private static void Delete(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

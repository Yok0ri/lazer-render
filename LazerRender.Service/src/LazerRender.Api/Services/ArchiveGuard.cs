using System.IO.Compression;

namespace LazerRender.Api.Services;

/// <summary>
/// Cheap pre-flight guard for uploaded archives (<c>.osk</c>, <c>.osz</c>) before they are handed to the
/// engine's third-party parsers (SharpCompress, HtmlAgilityPack, the lazer decoders), which run in the
/// process that holds the GPU context and the render credentials.
///
/// It reads only the zip central directory, so it is safe to run on untrusted input and cannot itself
/// be made to expand anything. It rejects packages that declare an implausible entry count, an
/// implausible total uncompressed size, or an extreme compression ratio — the shapes a decompression
/// bomb takes. This is a mitigation, not a sandbox: the engine is still parsing untrusted data.
/// </summary>
internal static class ArchiveGuard
{
    public const int MaxEntries = 5000;
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxCompressionRatio = 200;

    /// <summary>Returns a human-readable reason to reject the archive, or <c>null</c> when it looks sane.</summary>
    public static string? Validate(string path, long compressedBytes)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            if (archive.Entries.Count > MaxEntries)
                return $"the archive contains {archive.Entries.Count} entries (limit {MaxEntries}).";

            long totalUncompressed = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                totalUncompressed += entry.Length;

                if (totalUncompressed > MaxUncompressedBytes)
                {
                    return $"the archive expands to more than "
                           + $"{MaxUncompressedBytes / (1024 * 1024)} MB.";
                }
            }

            if (compressedBytes > 0 && totalUncompressed / compressedBytes > MaxCompressionRatio)
                return "the archive's compression ratio is implausible.";

            return null;
        }
        catch (InvalidDataException)
        {
            return "the archive is not a readable zip package.";
        }
        catch (IOException e)
        {
            return $"the archive could not be read: {e.Message}";
        }
    }
}

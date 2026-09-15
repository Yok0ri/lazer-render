namespace LazerRender.Api.Services;

public sealed record ReplayHeader(string BeatmapMd5, string? PlayerUsername);

/// <summary>
/// Reads the lazer-format <c>.osr</c> header: two LEB128 varints (ruleset id and format
/// version), then a <c>0x0b</c>-prefixed string (beatmap MD5) and a second string (username).
/// Mirrors LazerRender's own <c>readBeatmapHash</c> so validation matches the renderer.
/// </summary>
public static class ReplayFileParser
{
    public static ReplayHeader? TryParse(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            ReadVarint(stream); // ruleset id
            ReadVarint(stream); // format version

            string beatmapMd5 = ReadString(stream);
            string? username = null;

            try
            {
                username = ReadString(stream);
            }
            catch
            {
                // The username is optional for validation purposes.
            }

            if (string.IsNullOrWhiteSpace(beatmapMd5))
                return null;

            return new ReplayHeader(beatmapMd5, username);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsMd5Hash(string value) =>
        value.Length == 32 && value.All(Uri.IsHexDigit);

    private static string ReadString(Stream stream)
    {
        if (stream.ReadByte() != 0x0b)
            throw new InvalidDataException("Missing string marker in .osr header.");

        int length = ReadVarint(stream);

        if (length is < 0 or > 256)
            throw new InvalidDataException($"Implausible string length {length} in .osr header.");

        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    private static int ReadVarint(Stream stream)
    {
        int result = 0;
        int shift = 0;

        while (shift < 32)
        {
            int b = stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("Unexpected end of .osr header.");

            result |= (b & 0x7f) << shift;

            if ((b & 0x80) == 0)
                return result;

            shift += 7;
        }

        throw new InvalidDataException("Varint too long in .osr header.");
    }
}

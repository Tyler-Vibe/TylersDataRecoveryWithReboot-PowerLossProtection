using System.Buffers.Binary;
using DataRecovery.Core.Disk;

namespace DataRecovery.Core.Carving;

internal static class CarvingUtils
{
    public const long DefaultMaxFileSizeCap = 64L * 1024 * 1024 * 1024; // 64 GiB safety cap for structural carvers

    public static bool TryReadExact(IBlockSource source, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= source.Length)
        {
            return false;
        }

        int read = source.ReadAt(offset, buffer);
        return read == buffer.Length;
    }

    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> buffer, int index) =>
        BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(index, 4));

    public static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> buffer, int index) =>
        BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(index, 8));

    public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> buffer, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(index, 4));

    public static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> buffer, int index) =>
        BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(index, 8));

    public static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> buffer, int index) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(index, 2));

    /// <summary>4 ASCII bytes that look like a plausible box/chunk FourCC tag (printable range).</summary>
    public static bool LooksLikePrintableTag(ReadOnlySpan<byte> tag)
    {
        foreach (byte b in tag)
        {
            if (b < 0x20 || b > 0x7A)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Scans forward from <paramref name="start"/> for <paramref name="footer"/>, up to
    /// <paramref name="maxScanLength"/> bytes, reading in fixed-size windows via the block source.
    /// Returns the absolute offset of the LAST match found (useful for formats like PDF that may
    /// repeat their footer marker), or -1 if none found.
    /// </summary>
    public static long FindLastFooter(IBlockSource source, long start, byte[] footer, long maxScanLength, int windowSize = 4 * 1024 * 1024)
    {
        long lastMatch = -1;
        long end = Math.Min(source.Length, start + maxScanLength);
        int overlap = footer.Length - 1;
        var buffer = new byte[windowSize];

        long pos = start;
        while (pos < end)
        {
            int toRead = (int)Math.Min(windowSize, end - pos);
            int read = source.ReadAt(pos, buffer.AsSpan(0, toRead));
            if (read <= 0)
            {
                break;
            }

            var span = buffer.AsSpan(0, read);
            int searchFrom = 0;
            while (true)
            {
                int idx = span[searchFrom..].IndexOf(footer);
                if (idx < 0)
                {
                    break;
                }

                lastMatch = pos + searchFrom + idx;
                searchFrom += idx + 1;
                if (searchFrom >= span.Length)
                {
                    break;
                }
            }

            if (read < toRead)
            {
                break;
            }

            int advance = Math.Max(1, read - overlap);
            pos += advance;
        }

        return lastMatch;
    }

    /// <summary>Scans forward for the FIRST occurrence of <paramref name="footer"/> (simple case, no repeats expected).</summary>
    public static long FindFirstFooter(IBlockSource source, long start, byte[] footer, long maxScanLength, int windowSize = 4 * 1024 * 1024)
    {
        long end = Math.Min(source.Length, start + maxScanLength);
        int overlap = footer.Length - 1;
        var buffer = new byte[windowSize];

        long pos = start;
        while (pos < end)
        {
            int toRead = (int)Math.Min(windowSize, end - pos);
            int read = source.ReadAt(pos, buffer.AsSpan(0, toRead));
            if (read <= 0)
            {
                break;
            }

            int idx = buffer.AsSpan(0, read).IndexOf(footer);
            if (idx >= 0)
            {
                return pos + idx;
            }

            if (read < toRead)
            {
                break;
            }

            pos += Math.Max(1, read - overlap);
        }

        return -1;
    }
}

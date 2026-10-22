using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Carving;

/// <summary>
/// Recovers MKV/WebM files (EBML/Matroska container). Parses the EBML header element and the
/// following Segment element's size (a variable-length "vint") to determine the total file length.
/// Streaming muxers sometimes write an "unknown size" Segment (all-1s vint), in which case the exact
/// length can't be derived from headers alone - we fall back to a generous capped estimate.
/// </summary>
public sealed class MkvCarver : ISignatureCarver
{
    public FileTypeDefinition FileType => FileTypeDefinition.Mkv;

    public byte[] HeaderPattern { get; } = [0x1A, 0x45, 0xDF, 0xA3];

    public int HeaderPatternOffsetInFile => 0;

    private const long FallbackSize = 4L * 1024 * 1024 * 1024;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        long fileStart = patternOffset;

        // Read a generous window up front; EBML header elements are small (a few dozen bytes).
        Span<byte> buffer = stackalloc byte[64];
        if (!CarvingUtils.TryReadExact(source, fileStart, buffer))
        {
            return false;
        }

        if (!buffer[..4].SequenceEqual(HeaderPattern))
        {
            return false;
        }

        if (!TryReadVint(buffer[4..], isElementId: false, out long headerSize, out int headerSizeFieldLength))
        {
            return false;
        }

        long segmentIdOffset = fileStart + 4 + headerSizeFieldLength + headerSize;

        Span<byte> segmentBuf = stackalloc byte[16];
        if (!CarvingUtils.TryReadExact(source, segmentIdOffset, segmentBuf))
        {
            return false;
        }

        // Segment element ID = 0x18538067
        if (segmentBuf[0] != 0x18 || segmentBuf[1] != 0x53 || segmentBuf[2] != 0x80 || segmentBuf[3] != 0x67)
        {
            return false;
        }

        if (!TryReadVint(segmentBuf[4..], isElementId: false, out long segmentSize, out int segmentSizeFieldLength, out bool isUnknown))
        {
            return false;
        }

        long segmentDataStart = segmentIdOffset + 4 + segmentSizeFieldLength;

        if (isUnknown || segmentSize <= 0 || segmentSize > CarvingUtils.DefaultMaxFileSizeCap)
        {
            long fallback = Math.Min(segmentDataStart + FallbackSize, source.Length) - fileStart;
            result = new CarveResult(fileStart, fallback, false);
            return true;
        }

        long total = segmentDataStart + segmentSize - fileStart;
        long clamped = Math.Min(total, source.Length - fileStart);
        result = new CarveResult(fileStart, clamped, clamped == total);
        return true;
    }

    private static bool TryReadVint(ReadOnlySpan<byte> buffer, bool isElementId, out long value, out int fieldLength)
        => TryReadVint(buffer, isElementId, out value, out fieldLength, out _);

    /// <summary>
    /// Decodes an EBML variable-length integer: the number of leading zero bits in the first byte
    /// (before the first set bit) determines the total field length (1-8 bytes); the marker bit
    /// itself is stripped from the value.
    /// </summary>
    private static bool TryReadVint(ReadOnlySpan<byte> buffer, bool isElementId, out long value, out int fieldLength, out bool isUnknown)
    {
        value = 0;
        fieldLength = 0;
        isUnknown = false;

        if (buffer.Length == 0)
        {
            return false;
        }

        byte first = buffer[0];
        int length = 0;
        for (int bit = 7; bit >= 0; bit--)
        {
            if ((first & (1 << bit)) != 0)
            {
                length = 8 - bit;
                break;
            }
        }

        if (length == 0 || length > 8 || buffer.Length < length)
        {
            return false;
        }

        fieldLength = length;
        long mask = (1L << (8 - length)) - 1;
        long result = first & mask;
        bool allOnes = (first & mask) == mask;

        for (int i = 1; i < length; i++)
        {
            result = (result << 8) | buffer[i];
            allOnes &= buffer[i] == 0xFF;
        }

        value = result;
        isUnknown = !isElementId && allOnes;
        return true;
    }
}

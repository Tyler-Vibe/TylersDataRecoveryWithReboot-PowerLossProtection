using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Carving;

/// <summary>
/// Recovers MP4/MOV/M4V/3GP files (all use the ISO Base Media / QuickTime box container).
/// Determines the exact file length by walking top-level box headers (size+FourCC) - each box's
/// size field tells us exactly how many bytes to skip to reach the next one, so the whole file's
/// length can be computed from a handful of small positioned reads instead of reading the file's
/// (potentially multi-gigabyte) payload.
/// </summary>
public sealed class Mp4Carver : ISignatureCarver
{
    public FileTypeDefinition FileType => FileTypeDefinition.Mp4Family;

    public byte[] HeaderPattern { get; } = "ftyp"u8.ToArray();

    public int HeaderPatternOffsetInFile => 4;

    private const int MaxTopLevelBoxes = 10_000;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        long fileStart = patternOffset - HeaderPatternOffsetInFile;
        if (fileStart < 0)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        if (!CarvingUtils.TryReadExact(source, fileStart, header))
        {
            return false;
        }

        uint firstBoxSize = CarvingUtils.ReadUInt32BigEndian(header, 0);
        if (!header.Slice(4, 4).SequenceEqual(HeaderPattern))
        {
            return false;
        }

        if (!CarvingUtils.LooksLikePrintableTag(header.Slice(8, 4)))
        {
            return false; // major_brand should be printable ASCII; otherwise this is a false-positive match
        }

        if (firstBoxSize is < 8 or > 4096)
        {
            return false; // ftyp boxes are always small; anything else is not a real ftyp box
        }

        string extension = header[8] == (byte)'q' && header[9] == (byte)'t' ? ".mov" : ".mp4";

        long offset = fileStart;
        long totalSize = 0;
        bool sawSecondBox = false;

        Span<byte> boxHeader = stackalloc byte[8];
        Span<byte> ext = stackalloc byte[8];

        for (int i = 0; i < MaxTopLevelBoxes; i++)
        {
            if (!CarvingUtils.TryReadExact(source, offset, boxHeader))
            {
                break; // ran off the end of the device - use what we accumulated so far
            }

            uint boxSize32 = CarvingUtils.ReadUInt32BigEndian(boxHeader, 0);
            ReadOnlySpan<byte> tag = boxHeader.Slice(4, 4);

            if (!CarvingUtils.LooksLikePrintableTag(tag))
            {
                break; // hit non-box data - end of this file
            }

            long boxSize;
            if (boxSize32 == 1)
            {
                if (!CarvingUtils.TryReadExact(source, offset + 8, ext))
                {
                    break;
                }

                boxSize = (long)CarvingUtils.ReadUInt64BigEndian(ext, 0);
            }
            else if (boxSize32 == 0)
            {
                // Box extends to EOF (legal, but means we can't know the exact length cheaply).
                result = new CarveResult(fileStart, Math.Min(totalSize + CarvingUtils.DefaultMaxFileSizeCap, source.Length - fileStart), false, extension);
                return sawSecondBox || totalSize > 0;
            }
            else
            {
                boxSize = boxSize32;
            }

            if (boxSize < 8 || totalSize + boxSize > CarvingUtils.DefaultMaxFileSizeCap)
            {
                break;
            }

            totalSize += boxSize;
            offset = fileStart + totalSize;
            sawSecondBox = true;

            if (offset >= source.Length)
            {
                break;
            }
        }

        if (!sawSecondBox || totalSize < 16)
        {
            return false; // a lone ftyp box with nothing plausible after it - likely a false positive
        }

        result = new CarveResult(fileStart, totalSize, true, extension);
        return true;
    }
}

using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Carving;

/// <summary>
/// Recovers AVI files (RIFF container). The RIFF size field at file offset 4 gives the exact
/// total length directly - no need to walk chunks.
/// </summary>
public sealed class AviCarver : ISignatureCarver
{
    public FileTypeDefinition FileType => FileTypeDefinition.Avi;

    public byte[] HeaderPattern { get; } = "AVI "u8.ToArray();

    public int HeaderPatternOffsetInFile => 8;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        long fileStart = patternOffset - HeaderPatternOffsetInFile;
        if (fileStart < 0)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[12];
        if (!CarvingUtils.TryReadExact(source, fileStart, header))
        {
            return false;
        }

        if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual(HeaderPattern))
        {
            return false;
        }

        uint riffSize = CarvingUtils.ReadUInt32LittleEndian(header, 4);
        long total = 8L + riffSize;

        if (riffSize < 12 || total > CarvingUtils.DefaultMaxFileSizeCap || fileStart + total > source.Length)
        {
            // RIFF size field looks implausible or was truncated on disk - fall back to "up to EOF"
            // capped at a sane maximum, flagged as an inexact recovery.
            long clamped = Math.Min(total, source.Length - fileStart);
            if (clamped < 12)
            {
                return false;
            }

            result = new CarveResult(fileStart, clamped, false);
            return true;
        }

        result = new CarveResult(fileStart, total, true);
        return true;
    }
}

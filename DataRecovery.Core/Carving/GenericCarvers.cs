using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Carving;

/// <summary>
/// JPEG: header is the 3-byte SOI+marker sequence; footer is the 2-byte EOI marker (0xFFD9).
/// </summary>
public sealed class JpegCarver : ISignatureCarver
{
    private const long MaxScan = 200L * 1024 * 1024;

    public FileTypeDefinition FileType => FileTypeDefinition.Jpeg;

    public byte[] HeaderPattern { get; } = [0xFF, 0xD8, 0xFF];

    public int HeaderPatternOffsetInFile => 0;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        byte[] footer = [0xFF, 0xD9];
        long footerPos = CarvingUtils.FindFirstFooter(source, patternOffset + 3, footer, MaxScan);
        if (footerPos < 0)
        {
            return false;
        }

        long length = footerPos + 2 - patternOffset;
        result = new CarveResult(patternOffset, length, true);
        return true;
    }
}

/// <summary>
/// PNG: fixed 8-byte signature; footer is the fixed-content IEND chunk (length=0 + "IEND" + its
/// well-known CRC32), which is a constant 12-byte byte sequence.
/// </summary>
public sealed class PngCarver : ISignatureCarver
{
    private const long MaxScan = 500L * 1024 * 1024;

    public FileTypeDefinition FileType => FileTypeDefinition.Png;

    public byte[] HeaderPattern { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public int HeaderPatternOffsetInFile => 0;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        byte[] footer = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        long footerPos = CarvingUtils.FindFirstFooter(source, patternOffset + 8, footer, MaxScan);
        if (footerPos < 0)
        {
            return false;
        }

        long length = footerPos + footer.Length - patternOffset;
        result = new CarveResult(patternOffset, length, true);
        return true;
    }
}

/// <summary>
/// PDF: header is "%PDF-"; footer is the LAST "%%EOF" marker in the file (PDFs with incremental
/// updates repeat it, so we take the last occurrence to capture the complete file).
/// </summary>
public sealed class PdfCarver : ISignatureCarver
{
    private const long MaxScan = 500L * 1024 * 1024;

    public FileTypeDefinition FileType => FileTypeDefinition.Pdf;

    public byte[] HeaderPattern { get; } = "%PDF-"u8.ToArray();

    public int HeaderPatternOffsetInFile => 0;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        byte[] footer = "%%EOF"u8.ToArray();
        long footerPos = CarvingUtils.FindLastFooter(source, patternOffset + 5, footer, MaxScan);
        if (footerPos < 0)
        {
            return false;
        }

        long length = footerPos + footer.Length - patternOffset;
        result = new CarveResult(patternOffset, length, true);
        return true;
    }
}

/// <summary>
/// ZIP/DOCX/XLSX/PPTX (all are ZIP containers): header is the local file header signature;
/// footer is the End Of Central Directory record, whose comment-length field tells us exactly
/// where the archive truly ends.
/// </summary>
public sealed class ZipCarver : ISignatureCarver
{
    private const long MaxScan = 4L * 1024 * 1024 * 1024;

    public FileTypeDefinition FileType => FileTypeDefinition.Zip;

    public byte[] HeaderPattern { get; } = [0x50, 0x4B, 0x03, 0x04];

    public int HeaderPatternOffsetInFile => 0;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        byte[] eocd = [0x50, 0x4B, 0x05, 0x06];
        long eocdPos = CarvingUtils.FindFirstFooter(source, patternOffset + 4, eocd, MaxScan);
        if (eocdPos < 0)
        {
            return false;
        }

        Span<byte> tail = stackalloc byte[22];
        if (!CarvingUtils.TryReadExact(source, eocdPos, tail))
        {
            return false;
        }

        ushort commentLength = CarvingUtils.ReadUInt16LittleEndian(tail, 20);
        long length = eocdPos + 22 + commentLength - patternOffset;
        result = new CarveResult(patternOffset, length, true);
        return true;
    }
}

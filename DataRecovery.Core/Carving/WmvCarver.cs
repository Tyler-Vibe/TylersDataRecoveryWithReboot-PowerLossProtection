using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Carving;

/// <summary>
/// Recovers WMV/ASF files. The 16-byte ASF Header Object GUID is essentially unique (no realistic
/// false-positive risk). The exact total file size lives inside the nested "File Properties Object",
/// which we locate by walking the header's sub-objects.
/// </summary>
public sealed class WmvCarver : ISignatureCarver
{
    // Byte layout of the well-known ASF GUIDs, as they appear on disk.
    private static readonly byte[] HeaderObjectGuid =
    [
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C,
    ];

    private static readonly byte[] FilePropertiesObjectGuid =
    [
        0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65,
    ];

    public FileTypeDefinition FileType => FileTypeDefinition.Wmv;

    public byte[] HeaderPattern => HeaderObjectGuid;

    public int HeaderPatternOffsetInFile => 0;

    private const int MaxSubObjects = 256;

    public bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result)
    {
        result = default;
        long fileStart = patternOffset;

        Span<byte> preamble = stackalloc byte[30]; // GUID(16) + Size(8) + NumHeaderObjects(4) + Reserved(2)
        if (!CarvingUtils.TryReadExact(source, fileStart, preamble))
        {
            return false;
        }

        long headerObjectSize = (long)CarvingUtils.ReadUInt64LittleEndian(preamble, 16);
        if (headerObjectSize < 30 || headerObjectSize > CarvingUtils.DefaultMaxFileSizeCap)
        {
            return false;
        }

        long subObjectOffset = fileStart + 30;
        long headerEnd = fileStart + headerObjectSize;

        Span<byte> subHeader = stackalloc byte[24]; // GUID(16) + Size(8)
        for (int i = 0; i < MaxSubObjects && subObjectOffset + 24 <= headerEnd; i++)
        {
            if (!CarvingUtils.TryReadExact(source, subObjectOffset, subHeader))
            {
                break;
            }

            long subObjectSize = (long)CarvingUtils.ReadUInt64LittleEndian(subHeader, 16);
            if (subObjectSize < 24)
            {
                break;
            }

            if (subHeader[..16].SequenceEqual(FilePropertiesObjectGuid))
            {
                Span<byte> fileSizeField = stackalloc byte[8];
                // File Properties Object layout: GUID(16) + Size(8) + FileID(16) + FileSize(8) + ...
                if (!CarvingUtils.TryReadExact(source, subObjectOffset + 40, fileSizeField))
                {
                    break;
                }

                long totalFileSize = (long)CarvingUtils.ReadUInt64LittleEndian(fileSizeField, 0);
                if (totalFileSize >= headerObjectSize && totalFileSize <= CarvingUtils.DefaultMaxFileSizeCap)
                {
                    long clamped = Math.Min(totalFileSize, source.Length - fileStart);
                    result = new CarveResult(fileStart, clamped, clamped == totalFileSize);
                    return true;
                }

                break;
            }

            subObjectOffset += subObjectSize;
        }

        // Couldn't find a File Properties Object with a sane size - fall back to "header object
        // exists, but we don't know the true length"; flag as inexact using a generous cap.
        long fallback = Math.Min(headerObjectSize + 512L * 1024 * 1024, source.Length - fileStart);
        if (fallback < headerObjectSize)
        {
            return false;
        }

        result = new CarveResult(fileStart, fallback, false);
        return true;
    }
}

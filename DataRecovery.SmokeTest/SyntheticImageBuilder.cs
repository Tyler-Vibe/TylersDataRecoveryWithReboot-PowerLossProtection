using System.Text;

namespace DataRecovery.SmokeTest;

public sealed record EmbeddedFile(string TypeId, long Offset, long Length, byte[] Bytes);

/// <summary>
/// Builds a synthetic "disk image" (a plain file full of noise, standing in for unallocated
/// sectors) with known video/image/doc files embedded at known offsets, so the carving engine's
/// output can be checked byte-for-byte against ground truth.
/// </summary>
public static class SyntheticImageBuilder
{
    public static (byte[] Image, List<EmbeddedFile> Embedded) Build(int imageSize, int seed)
    {
        var rng = new Random(seed);
        var image = new byte[imageSize];
        rng.NextBytes(image); // noise, standing in for old/unrelated unallocated sector data

        var embedded = new List<EmbeddedFile>();

        void Place(int offset, byte[] bytes, string typeId)
        {
            Array.Copy(bytes, 0, image, offset, bytes.Length);
            embedded.Add(new EmbeddedFile(typeId, offset, bytes.Length, bytes));

            // Guarantee a non-printable/implausible run right after the embedded file so structural
            // box-walkers (MP4) can never spuriously wander past this file's true end, even in the
            // astronomically unlikely case that trailing noise looked like a plausible box/chunk tag.
            int guardStart = offset + bytes.Length;
            int guardLength = Math.Min(64, image.Length - guardStart);
            if (guardLength > 0)
            {
                Array.Clear(image, guardStart, guardLength);
            }
        }

        Place(4096, BuildMp4(3 * 1024 * 1024, rng), "mp4-family");
        Place(2 * 1024 * 1024 - 2, BuildJpeg(40_000, rng), "jpeg"); // straddles a 2MB chunk boundary on purpose
        Place(4 * 1024 * 1024 + 4096, BuildPng(60_000, rng), "png");
        Place(6 * 1024 * 1024, BuildAvi(1_500_000, rng), "avi");
        Place(9 * 1024 * 1024, BuildWmv(900_000, rng), "wmv");
        Place(11 * 1024 * 1024, BuildMkv(1_200_000, rng), "mkv");
        Place(14 * 1024 * 1024, BuildZip(500_000, rng), "zip");
        Place(17 * 1024 * 1024, BuildPdf(20_000, rng), "pdf");
        Place(20 * 1024 * 1024 - 3, BuildMp4(2 * 1024 * 1024, rng), "mp4-family"); // second MP4, also boundary-straddling

        return (image, embedded);
    }

    private static void WriteBox(MemoryStream outStream, string fourCc, Action<MemoryStream> writePayload)
    {
        using var payload = new MemoryStream();
        writePayload(payload);
        byte[] payloadBytes = payload.ToArray();
        WriteUInt32BE(outStream, (uint)(8 + payloadBytes.Length));
        outStream.Write(Encoding.ASCII.GetBytes(fourCc));
        outStream.Write(payloadBytes);
    }

    private static byte[] BuildMp4(int mdatSize, Random rng)
    {
        using var ms = new MemoryStream();
        WriteBox(ms, "ftyp", p =>
        {
            p.Write(Encoding.ASCII.GetBytes("isom"));
            WriteUInt32BE(p, 512);
            p.Write(Encoding.ASCII.GetBytes("isomiso2avc1mp41"));
        });
        WriteBox(ms, "free", _ => { });

        byte[] payload = new byte[mdatSize];
        rng.NextBytes(payload);
        WriteBox(ms, "mdat", p => p.Write(payload));
        WriteBox(ms, "moov", p => p.Write(Encoding.ASCII.GetBytes("fakemoovcontent-notreallyvalid-butthatsfine")));
        return ms.ToArray();
    }

    private static byte[] BuildJpeg(int contentSize, Random rng)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8, 0xFF, 0xE0]);
        ms.Write([0x00, 0x10]);
        ms.Write(Encoding.ASCII.GetBytes("JFIF\0"));
        ms.Write(new byte[11]);

        byte[] payload = new byte[contentSize];
        rng.NextBytes(payload);
        // Prevent an accidental EOI marker (FF D9) inside the "compressed data" noise, which would
        // make the carver (correctly) stop earlier than our expected length.
        for (int i = 0; i < payload.Length - 1; i++)
        {
            if (payload[i] == 0xFF && payload[i + 1] == 0xD9)
            {
                payload[i + 1] = 0x00;
            }
        }

        ms.Write(payload);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream outStream, string type, Action<MemoryStream> writePayload)
    {
        using var payload = new MemoryStream();
        writePayload(payload);
        byte[] payloadBytes = payload.ToArray();
        WriteUInt32BE(outStream, (uint)payloadBytes.Length);
        outStream.Write(Encoding.ASCII.GetBytes(type));
        outStream.Write(payloadBytes);
        outStream.Write(new byte[4]); // CRC placeholder, not validated by our carver
    }

    private static byte[] BuildPng(int idatSize, Random rng)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", p =>
        {
            WriteUInt32BE(p, 100);
            WriteUInt32BE(p, 100);
            p.Write([8, 2, 0, 0, 0]);
        });

        byte[] payload = new byte[idatSize];
        rng.NextBytes(payload);
        WriteChunk(ms, "IDAT", p => p.Write(payload));
        ms.Write([0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]);
        return ms.ToArray();
    }

    private static byte[] BuildAvi(int contentSize, Random rng)
    {
        using var ms = new MemoryStream();
        byte[] payload = new byte[contentSize];
        rng.NextBytes(payload);

        uint riffSize = (uint)(4 + payload.Length);
        ms.Write([0x52, 0x49, 0x46, 0x46]);
        WriteUInt32LE(ms, riffSize);
        ms.Write(Encoding.ASCII.GetBytes("AVI "));
        ms.Write(payload);
        return ms.ToArray();
    }

    private static byte[] BuildWmv(int totalFileSize, Random rng)
    {
        byte[] headerGuid = [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
        byte[] filePropGuid = [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

        const int fileProps = 48; // GUID(16) + Size(8) + FileID(16) + FileSize(8)
        long headerObjectSize = 30 + fileProps;

        using var ms = new MemoryStream();
        ms.Write(headerGuid);
        WriteUInt64LE(ms, (ulong)headerObjectSize);
        WriteUInt32LE(ms, 1);
        ms.Write([0, 0]);

        ms.Write(filePropGuid);
        WriteUInt64LE(ms, fileProps);
        ms.Write(new byte[16]); // FileID
        WriteUInt64LE(ms, (ulong)totalFileSize);

        byte[] result = ms.ToArray();
        var padded = new byte[totalFileSize];
        Array.Copy(result, padded, result.Length);
        var tail = new byte[totalFileSize - result.Length];
        rng.NextBytes(tail);
        Array.Copy(tail, 0, padded, result.Length, tail.Length);
        return padded;
    }

    private static void WriteEbmlVint8(MemoryStream ms, long value)
    {
        ms.WriteByte(0x01);
        var bytes = new byte[7];
        for (int i = 6; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        ms.Write(bytes);
    }

    private static byte[] BuildMkv(int segmentContentSize, Random rng)
    {
        using var ms = new MemoryStream();
        ms.Write([0x1A, 0x45, 0xDF, 0xA3]);
        byte[] headerContent = new byte[20];
        rng.NextBytes(headerContent);
        WriteEbmlVint8(ms, headerContent.Length);
        ms.Write(headerContent);

        ms.Write([0x18, 0x53, 0x80, 0x67]);
        WriteEbmlVint8(ms, segmentContentSize);
        byte[] segmentContent = new byte[segmentContentSize];
        rng.NextBytes(segmentContent);
        ms.Write(segmentContent);

        return ms.ToArray();
    }

    private static byte[] BuildZip(int contentSize, Random rng)
    {
        using var ms = new MemoryStream();
        ms.Write([0x50, 0x4B, 0x03, 0x04]);
        byte[] headerFields = new byte[26];
        rng.NextBytes(headerFields);
        ms.Write(headerFields);
        ms.Write(Encoding.ASCII.GetBytes("recovered.bin"));

        byte[] payload = new byte[contentSize];
        rng.NextBytes(payload);
        ms.Write(payload);

        ms.Write([0x50, 0x4B, 0x05, 0x06]);
        ms.Write(new byte[16]);
        WriteUInt16LE(ms, 0); // comment length = 0
        return ms.ToArray();
    }

    private static byte[] BuildPdf(int contentSize, Random rng)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        byte[] payload = new byte[contentSize];
        rng.NextBytes(payload);
        ms.Write(payload);
        ms.Write(Encoding.ASCII.GetBytes("\n%%EOF"));
        return ms.ToArray();
    }

    private static void WriteUInt32BE(MemoryStream ms, uint value)
    {
        ms.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    private static void WriteUInt32LE(MemoryStream ms, uint value)
    {
        ms.Write([(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)]);
    }

    private static void WriteUInt64LE(MemoryStream ms, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            ms.WriteByte((byte)(value & 0xFF));
            value >>= 8;
        }
    }

    private static void WriteUInt16LE(MemoryStream ms, ushort value)
    {
        ms.Write([(byte)value, (byte)(value >> 8)]);
    }
}

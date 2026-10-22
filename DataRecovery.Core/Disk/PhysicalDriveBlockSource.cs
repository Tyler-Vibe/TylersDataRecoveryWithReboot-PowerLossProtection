using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataRecovery.Core.Disk;

/// <summary>
/// Reads directly from a physical drive (<c>\\.\PhysicalDriveN</c>) using unbuffered I/O
/// (<c>FILE_FLAG_NO_BUFFERING</c>) for maximum sequential throughput, bypassing the OS cache
/// entirely (which would otherwise be useless - and wasteful - for a one-shot multi-terabyte scan).
/// </summary>
public sealed class PhysicalDriveBlockSource : IBlockSource
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public long Length { get; }

    public int SectorSize { get; }

    public PhysicalDriveBlockSource(string devicePath)
    {
        _handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to open '{devicePath}' (Win32 error {err}). " +
                "Raw physical-drive access requires running this app as Administrator.");
        }

        SectorSize = QuerySectorSize(_handle);
        Length = QueryLength(_handle);
    }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long alignedOffset = AlignDown(offset, SectorSize);
        int frontPadding = (int)(offset - alignedOffset);
        int alignedLength = AlignUp(frontPadding + destination.Length, SectorSize);

        // Fast path: caller already gave us a sector-aligned offset and length (true for every
        // main sequential chunk read - the only read done at high frequency).
        if (frontPadding == 0 && alignedLength == destination.Length)
        {
            return ReadAlignedClamped(alignedOffset, destination);
        }

        using var aligned = new AlignedBuffer(alignedLength, (nuint)SectorSize);
        int readBytes = ReadAlignedClamped(alignedOffset, aligned.Span);
        int available = Math.Max(0, readBytes - frontPadding);
        int toCopy = Math.Min(available, destination.Length);
        if (toCopy > 0)
        {
            aligned.Span.Slice(frontPadding, toCopy).CopyTo(destination);
        }

        return toCopy;
    }

    private int ReadAlignedClamped(long alignedOffset, Span<byte> alignedDestination)
    {
        long remaining = Length - alignedOffset;
        if (remaining <= 0)
        {
            return 0;
        }

        if (remaining >= alignedDestination.Length)
        {
            return System.IO.RandomAccess.Read(_handle, alignedDestination, alignedOffset);
        }

        // Near EOF: FILE_FLAG_NO_BUFFERING still requires a sector-aligned length, so read a full
        // aligned buffer but only report/copy the bytes that are actually within the device.
        int clampedLength = AlignUp((int)remaining, SectorSize);
        using var tail = new AlignedBuffer(clampedLength, (nuint)SectorSize);
        int read = System.IO.RandomAccess.Read(_handle, tail.Span, alignedOffset);
        int usable = (int)Math.Min(read, remaining);
        tail.Span[..usable].CopyTo(alignedDestination);
        return usable;
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);

    private static int AlignUp(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static unsafe int QuerySectorSize(SafeFileHandle handle)
    {
        var query = new NativeMethods.STORAGE_PROPERTY_QUERY
        {
            PropertyId = NativeMethods.STORAGE_PROPERTY_ID.StorageAccessAlignmentProperty,
            QueryType = NativeMethods.STORAGE_QUERY_TYPE.PropertyStandardQuery,
        };

        var descriptor = new NativeMethods.STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR();
        int querySize = Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>();
        int descSize = Marshal.SizeOf<NativeMethods.STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR>();

        IntPtr inBuf = Marshal.AllocHGlobal(querySize);
        IntPtr outBuf = Marshal.AllocHGlobal(descSize);
        try
        {
            Marshal.StructureToPtr(query, inBuf, false);
            bool ok = NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                inBuf, querySize,
                outBuf, descSize,
                out _, IntPtr.Zero) != 0;

            if (ok)
            {
                descriptor = Marshal.PtrToStructure<NativeMethods.STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR>(outBuf);
                if (descriptor.BytesPerPhysicalSector > 0)
                {
                    return (int)descriptor.BytesPerPhysicalSector;
                }

                if (descriptor.BytesPerLogicalSector > 0)
                {
                    return (int)descriptor.BytesPerLogicalSector;
                }
            }
        }
        catch
        {
            // Fall through to default below - some drivers/virtual disks don't support this query.
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }

        return 4096;
    }

    private static long QueryLength(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<NativeMethods.GET_LENGTH_INFORMATION>();
        IntPtr outBuf = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero, 0,
                outBuf, size,
                out _, IntPtr.Zero) != 0;

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"IOCTL_DISK_GET_LENGTH_INFO failed (Win32 error {err}).");
            }

            var info = Marshal.PtrToStructure<NativeMethods.GET_LENGTH_INFORMATION>(outBuf);
            return info.Length;
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}

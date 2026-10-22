using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataRecovery.Core.Disk;

/// <summary>
/// Raw Win32 P/Invoke surface needed to open and read a physical drive directly
/// (bypassing the file system), and to query its size and sector alignment.
/// </summary>
internal static partial class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;

    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    public struct GET_LENGTH_INFORMATION
    {
        public long Length;
    }

    public enum STORAGE_PROPERTY_ID
    {
        StorageDeviceProperty = 0,
        StorageAccessAlignmentProperty = 6,
    }

    public enum STORAGE_QUERY_TYPE
    {
        PropertyStandardQuery = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROPERTY_QUERY
    {
        public STORAGE_PROPERTY_ID PropertyId;
        public STORAGE_QUERY_TYPE QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public uint BytesPerCacheLine;
        public uint BytesOffsetForCacheAlignment;
        public uint BytesPerLogicalSector;
        public uint BytesPerPhysicalSector;
        public uint BytesOffsetForSectorAlignment;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    // Positional reads/writes against the resulting SafeFileHandle are done via the
    // System.IO.RandomAccess APIs rather than hand-rolled ReadFile/SetFilePointerEx P/Invokes.

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}

using Microsoft.Win32.SafeHandles;

namespace DataRecovery.Core.Disk;

/// <summary>
/// Reads from a plain file as if it were a block device. Used by the smoke test to exercise the
/// scan pipeline and carvers against a synthetic disk image, and could also be pointed at a
/// forensic <c>.img</c>/<c>.dd</c> image file of a real drive.
/// </summary>
public sealed class FileBlockSource : IBlockSource
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public long Length { get; }

    public int SectorSize { get; } = 512;

    public FileBlockSource(string filePath)
    {
        _handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long remaining = Length - offset;
        if (remaining <= 0)
        {
            return 0;
        }

        int toRead = (int)Math.Min(destination.Length, remaining);
        return RandomAccess.Read(_handle, destination[..toRead], offset);
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

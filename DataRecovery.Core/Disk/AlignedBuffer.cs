using System.Runtime.InteropServices;

namespace DataRecovery.Core.Disk;

/// <summary>
/// A native, sector-aligned memory block. <c>FILE_FLAG_NO_BUFFERING</c> requires the destination
/// buffer's memory address (not just the file offset/length) to be aligned to the drive's sector
/// size, so plain managed <c>byte[]</c> arrays cannot be used directly as read targets.
/// </summary>
public sealed unsafe class AlignedBuffer : IDisposable
{
    private void* _pointer;
    private bool _disposed;

    public int Length { get; }

    public AlignedBuffer(int length, nuint alignment)
    {
        Length = length;
        _pointer = NativeMemory.AlignedAlloc((nuint)length, alignment);
        if (_pointer == null)
        {
            throw new OutOfMemoryException($"Failed to allocate {length} aligned bytes.");
        }
    }

    public IntPtr Pointer => (IntPtr)_pointer;

    public Span<byte> Span => new(_pointer, Length);

    public byte[] ToArray(int count)
    {
        var result = new byte[count];
        Span[..count].CopyTo(result);
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer != null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }

        GC.SuppressFinalize(this);
    }

    ~AlignedBuffer()
    {
        Dispose();
    }
}

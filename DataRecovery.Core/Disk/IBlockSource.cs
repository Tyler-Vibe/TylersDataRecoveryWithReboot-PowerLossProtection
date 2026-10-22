namespace DataRecovery.Core.Disk;

/// <summary>
/// Abstraction over "a big block device we can read from at arbitrary offsets", implemented by
/// either the raw physical drive (production) or a plain file (used by the smoke test against a
/// synthetic disk image, since we cannot safely test against a real drive).
/// </summary>
public interface IBlockSource : IDisposable
{
    long Length { get; }

    /// <summary>
    /// Sector size to align offsets/lengths to for best throughput. Reads that aren't aligned
    /// are still handled correctly (internally padded), just less efficiently.
    /// </summary>
    int SectorSize { get; }

    /// <summary>
    /// Positional read, safe to call concurrently from multiple threads at different offsets
    /// (used both by the single sequential-scan reader and by concurrent probe/extraction reads).
    /// Returns the number of bytes actually read (may be less than destination.Length at EOF).
    /// </summary>
    int ReadAt(long offset, Span<byte> destination);
}

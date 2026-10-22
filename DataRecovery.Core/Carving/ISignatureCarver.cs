using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Carving;

public readonly record struct CarveResult(long FileStartOffset, long Length, bool LengthIsExact, string? ExtensionOverride = null);

/// <summary>
/// Detects one file format from a raw byte signature and determines the recovered file's length.
///
/// Video/container carvers (MP4, AVI, MKV, WMV) are "structural": they parse the container's own
/// box/chunk/element size fields via a handful of small positioned reads (<see cref="IBlockSource.ReadAt"/>)
/// to compute an exact length cheaply, without needing to hold the file's bytes in memory.
///
/// Generic carvers (JPEG/PNG/PDF/ZIP) are "streaming": they scan forward from the header for a
/// known footer signature, up to a bounded cap.
/// </summary>
public interface ISignatureCarver
{
    FileTypeDefinition FileType { get; }

    /// <summary>Byte pattern searched for in the raw stream.</summary>
    byte[] HeaderPattern { get; }

    /// <summary>
    /// Offset of <see cref="HeaderPattern"/> relative to the start of the file it identifies
    /// (0 for formats whose signature is the very first bytes of the file; non-zero for formats
    /// like MP4 where the searched pattern sits a few bytes into the file).
    /// </summary>
    int HeaderPatternOffsetInFile { get; }

    /// <summary>
    /// Called when <see cref="HeaderPattern"/> was found at absolute offset <paramref name="patternOffset"/>
    /// in the source. Validates the surrounding structure (rejecting false positives) and determines
    /// the recovered file length. Returns false if this was not actually a valid file of this type.
    /// </summary>
    bool TryCarve(IBlockSource source, long patternOffset, out CarveResult result);
}

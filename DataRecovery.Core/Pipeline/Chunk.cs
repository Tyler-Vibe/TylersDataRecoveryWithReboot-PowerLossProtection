namespace DataRecovery.Core.Pipeline;

/// <summary>
/// One unit of work flowing from the reader thread to the carve-worker pool.
///
/// The buffer holds <see cref="NominalLength"/> bytes belonging to this chunk's own region of the
/// disk, PLUS a small lookahead tail (<see cref="LookaheadLength"/>) borrowed from the start of the
/// next chunk. The lookahead exists purely so a header signature starting near the very end of this
/// chunk's nominal region still has enough trailing bytes available to be confirmed/parsed - workers
/// must only report matches starting at buffer index &lt; NominalLength, which guarantees each
/// absolute offset on the disk is considered by exactly one chunk (no duplicates, no gaps).
/// </summary>
public sealed class Chunk
{
    public required long Sequence { get; init; }
    public required long Offset { get; init; }
    public required byte[] Buffer { get; init; }
    public required int NominalLength { get; set; }
    public required int TotalValidLength { get; set; }

    public long EndOffset => Offset + NominalLength;
}

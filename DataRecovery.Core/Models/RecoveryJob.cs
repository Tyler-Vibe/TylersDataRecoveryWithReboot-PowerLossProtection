namespace DataRecovery.Core.Models;

public enum RecoveryJobStatus
{
    Detected,
    Extracting,
    Completed,
    Failed,
}

/// <summary>
/// One carve candidate found on the source drive: a byte range that looks like a recoverable file.
/// Persisted so extraction can resume/retry independently of scan position.
/// </summary>
public sealed class RecoveryJob
{
    public long Id { get; set; }
    public required string FileTypeId { get; set; }
    public required string Extension { get; set; }
    public long SourceOffset { get; set; }
    public long Length { get; set; }
    public bool LengthIsExact { get; set; }
    public RecoveryJobStatus Status { get; set; } = RecoveryJobStatus.Detected;
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }

    public string SuggestedFileName => $"recovered_{Id:D6}_{SourceOffset:X}{Extension}";

    public string SizeDisplay
    {
        get
        {
            const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
            return Length switch
            {
                >= (long)gb => $"{Length / gb:0.##} GB",
                >= (long)mb => $"{Length / mb:0.#} MB",
                >= (long)kb => $"{Length / kb:0.#} KB",
                _ => $"{Length} B",
            };
        }
    }

    public string OffsetDisplay => $"0x{SourceOffset:X}";
}

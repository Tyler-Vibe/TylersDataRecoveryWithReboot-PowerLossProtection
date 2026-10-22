namespace DataRecovery.Core.Models;

/// <summary>
/// Lightweight, frequently-updated stats used to drive the live GUI display.
/// Deliberately does not hold the full found-files list (that is queried from the DB/pipeline on demand).
/// </summary>
public sealed record ProgressSnapshot(
    long BytesScanned,
    long TotalBytes,
    double InstantThroughputMBps,
    double AverageThroughputMBps,
    TimeSpan Elapsed,
    TimeSpan? EstimatedTimeRemaining,
    int FilesDetected,
    int FilesCompleted,
    int FilesFailed,
    bool IsPaused,
    bool IsRunning)
{
    public double PercentComplete => TotalBytes <= 0 ? 0 : Math.Min(100.0, BytesScanned * 100.0 / TotalBytes);
}

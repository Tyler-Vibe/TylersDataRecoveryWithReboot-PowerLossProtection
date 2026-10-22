namespace DataRecovery.Core.Models;

/// <summary>
/// Identifies and describes a single resumable recovery session, persisted alongside the
/// checkpoint database so a relaunch of the app can detect and offer to resume it.
/// </summary>
public sealed class ScanSession
{
    public required string SourceDevicePath { get; set; }
    public long SourceSizeBytes { get; set; }
    public required string OutputFolder { get; set; }
    public required IReadOnlyList<string> SelectedFileTypeIds { get; set; }
    public long LastCheckpointOffset { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

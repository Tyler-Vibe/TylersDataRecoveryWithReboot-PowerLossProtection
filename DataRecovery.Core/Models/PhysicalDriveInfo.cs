namespace DataRecovery.Core.Models;

/// <summary>
/// Describes one physical drive as seen by Windows, for display in the drive-picker UI.
/// </summary>
public sealed record PhysicalDriveInfo(
    int Index,
    string DevicePath,
    string Model,
    long SizeBytes,
    string MediaType,
    IReadOnlyList<string> VolumeLetters)
{
    public string DisplayText =>
        $"\\\\.\\PhysicalDrive{Index}  -  {Model}  -  {FormatSize(SizeBytes)}" +
        (VolumeLetters.Count > 0 ? $"  [{string.Join(", ", VolumeLetters)}]" : "  [no drive letters - likely deleted/unallocated]");

    private static string FormatSize(long bytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        const double tb = gb * 1024;
        return bytes >= tb ? $"{bytes / tb:0.##} TB" : $"{bytes / gb:0.#} GB";
    }
}

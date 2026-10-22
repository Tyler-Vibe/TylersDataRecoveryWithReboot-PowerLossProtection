using System.Management;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Disk;

/// <summary>
/// Lists physical drives visible to Windows, for the drive-picker UI. Uses WMI (Win32_DiskDrive /
/// Win32_LogicalDisk) purely for friendly display data (model, size, drive letters currently
/// mounted on it) - actual reading of the drive is done separately via <see cref="PhysicalDriveBlockSource"/>.
/// </summary>
public static class DiskEnumerator
{
    public static List<PhysicalDriveInfo> ListPhysicalDrives()
    {
        var result = new List<PhysicalDriveInfo>();

        using var driveSearcher = new ManagementObjectSearcher(
            "SELECT Index, Model, Size, MediaType, DeviceID FROM Win32_DiskDrive");

        foreach (ManagementObject drive in driveSearcher.Get())
        {
            int index = Convert.ToInt32(drive["Index"]);
            string model = drive["Model"]?.ToString()?.Trim() ?? "Unknown drive";
            long size = drive["Size"] is null ? 0 : Convert.ToInt64(drive["Size"]);
            string mediaType = drive["MediaType"]?.ToString() ?? "Unknown";
            string deviceId = drive["DeviceID"]?.ToString() ?? $@"\\.\PhysicalDrive{index}";

            var letters = GetVolumeLettersForDisk(index);

            result.Add(new PhysicalDriveInfo(index, deviceId, model, size, mediaType, letters));
        }

        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    private static List<string> GetVolumeLettersForDisk(int diskIndex)
    {
        var letters = new List<string>();
        try
        {
            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{diskIndex}'}} " +
                "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using var logicalDiskSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                    "WHERE AssocClass = Win32_LogicalDiskToPartition");

                foreach (ManagementObject logicalDisk in logicalDiskSearcher.Get())
                {
                    string? letter = logicalDisk["DeviceID"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(letter))
                    {
                        letters.Add(letter);
                    }
                }
            }
        }
        catch
        {
            // Best-effort only; an empty list just means "no live volumes on this disk",
            // which is exactly the expected case for the deleted filesystems we're targeting.
        }

        return letters;
    }
}

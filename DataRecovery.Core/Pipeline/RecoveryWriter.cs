using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Pipeline;

/// <summary>
/// Extracts one recovered file's bytes from the source device to the output folder.
/// Writes to a temp file and only renames to the final name once the full length has been
/// written successfully, so a crash mid-write never leaves behind a file that looks "done".
/// </summary>
internal static class RecoveryWriter
{
    private const int CopyBufferSize = 4 * 1024 * 1024;

    public static void Extract(IBlockSource source, RecoveryJob job, string outputFolder)
    {
        string finalPath = Path.Combine(outputFolder, job.SuggestedFileName);
        string tempPath = finalPath + ".partial";

        byte[] buffer = new byte[CopyBufferSize];
        using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
        {
            long remaining = job.Length;
            long offset = job.SourceOffset;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = source.ReadAt(offset, buffer.AsSpan(0, toRead));
                if (read <= 0)
                {
                    throw new IOException($"Unexpected end of source data while extracting at offset {offset}.");
                }

                output.Write(buffer, 0, read);
                offset += read;
                remaining -= read;
            }

            output.Flush(flushToDisk: true);
        }

        if (File.Exists(finalPath))
        {
            finalPath = MakeUniquePath(finalPath);
        }

        File.Move(tempPath, finalPath);
        job.OutputPath = finalPath;
    }

    private static string MakeUniquePath(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

using DataRecovery.Core.Carving;
using DataRecovery.Core.Disk;
using DataRecovery.Core.Persistence;

namespace DataRecovery.Core.Pipeline;

public sealed class ScanPipelineOptions
{
    public required IBlockSource Source { get; init; }
    public string SourceDevicePath { get; init; } = "";
    public required string OutputFolder { get; init; }
    public required List<ISignatureCarver> Carvers { get; init; }
    public required RecoveryDatabase Database { get; init; }
    public long StartOffset { get; init; }
    public int ChunkSizeBytes { get; init; } = 8 * 1024 * 1024;
    public int CarveWorkerCount { get; init; } = Math.Max(2, Environment.ProcessorCount);
    public int WriterThreadCount { get; init; } = 3;
    public int ChunkQueueCapacity { get; init; } = 12;
    public int JobQueueCapacity { get; init; } = 4096;
}

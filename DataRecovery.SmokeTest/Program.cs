using DataRecovery.Core.Carving;
using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;
using DataRecovery.Core.Persistence;
using DataRecovery.Core.Pipeline;
using DataRecovery.SmokeTest;

Console.WriteLine("=== DataRecovery smoke test ===");
Console.WriteLine("Building synthetic disk image with known embedded files...");

const int imageSize = 24 * 1024 * 1024;
var (image, embedded) = SyntheticImageBuilder.Build(imageSize, seed: 12345);

string workDir = Path.Combine(Path.GetTempPath(), "DataRecoverySmokeTest_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(workDir);
string imagePath = Path.Combine(workDir, "synthetic.img");
string outputFolder = Path.Combine(workDir, "output");
string dbPath = Path.Combine(workDir, "session.db");

File.WriteAllBytes(imagePath, image);
Console.WriteLine($"Work dir: {workDir}");
Console.WriteLine($"Embedded {embedded.Count} synthetic files, image size {imageSize:N0} bytes.\n");

bool allPassed = true;

allPassed &= RunFullScanScenario(imagePath, outputFolder, dbPath, embedded, image);

// A much larger image (same embedded files, lots of extra trailing noise) so a mid-scan stop is
// reliably observable regardless of how fast the machine running this test is.
const int bigImageSize = 800 * 1024 * 1024;
var (bigImage, bigEmbedded) = SyntheticImageBuilder.Build(bigImageSize, seed: 12345);
string bigImagePath = Path.Combine(workDir, "synthetic_big.img");
File.WriteAllBytes(bigImagePath, bigImage);

allPassed &= RunStopAndResumeScenario(bigImagePath, workDir, bigEmbedded, bigImage);

Console.WriteLine();
Console.WriteLine(allPassed ? "=== ALL SCENARIOS PASSED ===" : "=== SOME SCENARIOS FAILED ===");
return allPassed ? 0 : 1;

static bool RunFullScanScenario(string imagePath, string outputFolder, string dbPath, List<EmbeddedFile> embedded, byte[] originalImage)
{
    Console.WriteLine("--- Scenario 1: full scan, single pass ---");

    using var source = new FileBlockSource(imagePath);
    using var db = new RecoveryDatabase(dbPath);

    var options = new ScanPipelineOptions
    {
        Source = source,
        OutputFolder = outputFolder,
        Carvers = CarverRegistry.AllCarvers.ToList(),
        Database = db,
        ChunkSizeBytes = 2 * 1024 * 1024, // deliberately small so several embedded files straddle chunk boundaries
    };

    var pipeline = new ScanPipeline(options);
    pipeline.LogMessage += msg => Console.WriteLine($"  [pipeline] {msg}");

    pipeline.Start();
    pipeline.WaitForCompletionAsync().GetAwaiter().GetResult();

    return VerifyResults(db, embedded, originalImage, "Scenario 1");
}

static bool RunStopAndResumeScenario(string imagePath, string workDir, List<EmbeddedFile> embedded, byte[] originalImage)
{
    Console.WriteLine("\n--- Scenario 2: stop midway, then resume from checkpoint ---");

    string outputFolder = Path.Combine(workDir, "output_resume");
    string dbPath = Path.Combine(workDir, "session_resume.db");

    using var source1 = new FileBlockSource(imagePath);
    var db1 = new RecoveryDatabase(dbPath);

    var options1 = new ScanPipelineOptions
    {
        Source = source1,
        OutputFolder = outputFolder,
        Carvers = CarverRegistry.AllCarvers.ToList(),
        Database = db1,
        ChunkSizeBytes = 1 * 1024 * 1024,
    };

    var pipeline1 = new ScanPipeline(options1);
    pipeline1.Start();

    // Let it run until it's partway through, then stop mid-scan to simulate a crash/shutdown.
    var timeout = DateTime.UtcNow.AddSeconds(30);
    while (pipeline1.GetSnapshot() is { PercentComplete: < 35.0, IsRunning: true } && DateTime.UtcNow < timeout)
    {
        Thread.Sleep(2);
    }

    pipeline1.StopAsync().GetAwaiter().GetResult();
    db1.Dispose();

    long checkpointAfterStop;
    using (var checkDb = new RecoveryDatabase(dbPath))
    {
        checkpointAfterStop = checkDb.LoadSession()?.LastCheckpointOffset ?? -1;
    }

    Console.WriteLine($"  Stopped after partial scan. Checkpoint offset = {checkpointAfterStop:N0} of {originalImage.Length:N0}.");

    if (checkpointAfterStop <= 0 || checkpointAfterStop >= originalImage.Length)
    {
        Console.WriteLine("  FAIL: expected a partial (non-zero, non-complete) checkpoint before resuming.");
        return false;
    }

    // Resume: brand new pipeline/source instance, same database and output folder, starting where we left off.
    using var source2 = new FileBlockSource(imagePath);
    using var db2 = new RecoveryDatabase(dbPath);
    var session = db2.LoadSession()!;

    var options2 = new ScanPipelineOptions
    {
        Source = source2,
        OutputFolder = outputFolder,
        Carvers = CarverRegistry.AllCarvers.ToList(),
        Database = db2,
        ChunkSizeBytes = 1 * 1024 * 1024,
        StartOffset = session.LastCheckpointOffset,
    };

    var pipeline2 = new ScanPipeline(options2);
    pipeline2.LogMessage += msg => Console.WriteLine($"  [resume] {msg}");
    pipeline2.Start();
    pipeline2.WaitForCompletionAsync().GetAwaiter().GetResult();

    return VerifyResults(db2, embedded, originalImage, "Scenario 2 (resumed)");
}

static bool VerifyResults(RecoveryDatabase db, List<EmbeddedFile> embedded, byte[] originalImage, string label)
{
    bool passed = true;
    var jobs = db.GetJobsByStatuses();
    Console.WriteLine($"  [{label}] Recovered {jobs.Count} candidate file(s) (expected at least {embedded.Count}).");

    foreach (var expected in embedded)
    {
        var match = jobs.FirstOrDefault(j => j.SourceOffset == expected.Offset);
        if (match is null)
        {
            Console.WriteLine($"  FAIL [{label}]: no recovered file found at offset {expected.Offset} (type {expected.TypeId}).");
            passed = false;
            continue;
        }

        if (match.Status != RecoveryJobStatus.Completed)
        {
            Console.WriteLine($"  FAIL [{label}]: job at offset {expected.Offset} ended in status {match.Status} ({match.ErrorMessage}).");
            passed = false;
            continue;
        }

        if (match.Length != expected.Length)
        {
            Console.WriteLine($"  FAIL [{label}]: length mismatch at offset {expected.Offset}: expected {expected.Length}, got {match.Length}.");
            passed = false;
            continue;
        }

        if (match.OutputPath is null || !File.Exists(match.OutputPath))
        {
            Console.WriteLine($"  FAIL [{label}]: output file missing for offset {expected.Offset}.");
            passed = false;
            continue;
        }

        byte[] recoveredBytes = File.ReadAllBytes(match.OutputPath);
        byte[] expectedBytes = originalImage.AsSpan((int)expected.Offset, (int)expected.Length).ToArray();
        if (!recoveredBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            Console.WriteLine($"  FAIL [{label}]: byte content mismatch for {expected.TypeId} at offset {expected.Offset}.");
            passed = false;
            continue;
        }

        Console.WriteLine($"  OK   [{label}]: {expected.TypeId,-12} offset={expected.Offset,-10} length={expected.Length,-10} -> {Path.GetFileName(match.OutputPath)} (byte-exact match)");
    }

    if (passed)
    {
        Console.WriteLine($"  [{label}] PASSED - all {embedded.Count} embedded files recovered correctly and byte-exact.");
    }

    return passed;
}

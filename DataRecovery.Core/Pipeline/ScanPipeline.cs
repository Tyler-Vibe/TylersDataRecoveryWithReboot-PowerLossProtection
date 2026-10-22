using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using DataRecovery.Core.Carving;
using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Pipeline;

/// <summary>
/// Orchestrates the full recovery pipeline:
///
///   1 sequential reader thread -&gt; bounded chunk channel -&gt; N carve-worker threads
///     -&gt; bounded job channel -&gt; M writer threads -&gt; output folder
///
/// with continuous checkpointing to <see cref="Persistence.RecoveryDatabase"/> so the whole scan
/// can be safely resumed after a restart. See the "Why multithreaded does not mean many threads
/// randomly reading the disk" note in the project plan for the reasoning behind this shape.
/// </summary>
public sealed class ScanPipeline : IDisposable
{
    private readonly ScanPipelineOptions _options;
    private readonly Channel<Chunk> _chunkChannel;
    private readonly Channel<RecoveryJob> _jobChannel;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private readonly HashSet<long> _knownOffsets = [];
    private readonly object _knownOffsetsLock = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly Stopwatch _activeStopwatch = new();
    private readonly Stopwatch _instantThroughputWatch = new();
    private readonly object _snapshotLock = new();

    private CheckpointTracker _checkpointTracker = null!;
    private Thread? _readerThread;
    private Thread[] _carveThreads = [];
    private Thread[] _writerThreads = [];
    private Thread? _checkpointThread;
    private Task _completionTask = Task.CompletedTask;
    private volatile bool _stopRequested;
    private volatile bool _disposed;

    private long _bytesScanned;
    private long _lastInstantBytes;
    private int _filesDetected;
    private int _filesCompleted;
    private int _filesFailed;

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }

    public event Action<RecoveryJob>? JobUpdated;
    public event Action<string>? LogMessage;

    public ScanPipeline(ScanPipelineOptions options)
    {
        _options = options;
        _chunkChannel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(options.ChunkQueueCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _jobChannel = Channel.CreateBounded<RecoveryJob>(new BoundedChannelOptions(options.JobQueueCapacity)
        {
            SingleWriter = false,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        Directory.CreateDirectory(_options.OutputFolder);

        // Seed dedup + resume state from anything already recorded by a previous run.
        foreach (var job in _options.Database.GetJobsByStatuses())
        {
            lock (_knownOffsetsLock)
            {
                _knownOffsets.Add(job.SourceOffset);
            }
        }

        var (detected, extracting, completed, failed) = _options.Database.GetStatusCounts();
        _filesDetected = detected + extracting + completed + failed;
        _filesCompleted = completed;
        _filesFailed = failed;

        // Re-queue anything that was detected/mid-extraction when the app last stopped.
        foreach (var pending in _options.Database.GetJobsByStatuses(RecoveryJobStatus.Detected, RecoveryJobStatus.Extracting))
        {
            _jobChannel.Writer.TryWrite(pending);
        }

        // Make sure a session row always exists, even if the caller didn't explicitly save one -
        // this is what makes a scan resumable purely by pointing a new pipeline at the same DB file.
        if (_options.Database.LoadSession() is null)
        {
            _options.Database.SaveSession(new ScanSession
            {
                SourceDevicePath = _options.SourceDevicePath,
                SourceSizeBytes = _options.Source.Length,
                OutputFolder = _options.OutputFolder,
                SelectedFileTypeIds = _options.Carvers.Select(c => c.FileType.Id).ToList(),
                LastCheckpointOffset = _options.StartOffset,
            });
        }

        long chunkSize = _options.ChunkSizeBytes;
        long startingSequence = _options.StartOffset / chunkSize;
        _checkpointTracker = new CheckpointTracker(startingSequence, _options.StartOffset);
        _bytesScanned = _options.StartOffset;

        _stopRequested = false;
        IsRunning = true;
        IsPaused = false;
        _stopwatch.Start();
        _activeStopwatch.Start();
        _instantThroughputWatch.Start();

        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "DataRecovery-Reader" };
        _readerThread.Start();

        _carveThreads = new Thread[_options.CarveWorkerCount];
        for (int i = 0; i < _carveThreads.Length; i++)
        {
            _carveThreads[i] = new Thread(CarveWorkerLoop) { IsBackground = true, Name = $"DataRecovery-Carve-{i}" };
            _carveThreads[i].Start();
        }

        _writerThreads = new Thread[_options.WriterThreadCount];
        for (int i = 0; i < _writerThreads.Length; i++)
        {
            _writerThreads[i] = new Thread(WriterLoop) { IsBackground = true, Name = $"DataRecovery-Writer-{i}" };
            _writerThreads[i].Start();
        }

        _checkpointThread = new Thread(CheckpointLoop) { IsBackground = true, Name = "DataRecovery-Checkpoint" };
        _checkpointThread.Start();

        _completionTask = Task.Run(RunToCompletion);

        Log($"Scan started at offset {_options.StartOffset:N0} of {_options.Source.Length:N0} bytes.");
    }

    /// <summary>
    /// Waits for the reader, carve workers, and writers to fully drain - whether that's because the
    /// whole drive was scanned (natural completion) or because <see cref="StopAsync"/> requested an
    /// early stop - and then finalizes the checkpoint and flips <see cref="IsRunning"/> off.
    /// </summary>
    private void RunToCompletion()
    {
        _readerThread?.Join();
        Array.ForEach(_carveThreads, t => t.Join());
        _jobChannel.Writer.TryComplete();
        Array.ForEach(_writerThreads, t => t.Join());

        _stopwatch.Stop();
        _activeStopwatch.Stop();
        _options.Database.UpdateCheckpoint(_checkpointTracker.SafeOffset);
        IsRunning = false;
        IsPaused = false;

        bool completedWholeDrive = !_stopRequested;
        Log(completedWholeDrive
            ? "Scan complete - the entire drive has been scanned."
            : $"Stopped. Checkpoint saved at offset {_checkpointTracker.SafeOffset:N0}.");
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused)
        {
            return;
        }

        IsPaused = true;
        _pauseGate.Reset();
        _activeStopwatch.Stop();
        Log("Paused.");
    }

    public void Resume()
    {
        if (!IsRunning || !IsPaused)
        {
            return;
        }

        IsPaused = false;
        _pauseGate.Set();
        _activeStopwatch.Start();
        Log("Resumed.");
    }

    /// <summary>Gracefully drains all in-flight work, commits a final checkpoint, and stops all threads.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        Log("Stopping - draining in-flight work...");
        _stopRequested = true;
        _pauseGate.Set(); // make sure a paused reader thread can observe the stop request

        await _completionTask;
        await Task.Run(() => _checkpointThread?.Join());
    }

    /// <summary>Completes when the scan finishes, whether that's natural end-of-drive or an explicit <see cref="StopAsync"/>.</summary>
    public Task WaitForCompletionAsync() => _completionTask;

    public ProgressSnapshot GetSnapshot()
    {
        long scanned = Interlocked.Read(ref _bytesScanned);
        // Use active (non-paused) time for throughput/ETA math - the wall-clock stopwatch keeps
        // running while paused, which would otherwise silently drag the lifetime average (and
        // therefore the ETA) toward zero for every minute the scan sat idle.
        double activeSeconds = Math.Max(0.001, _activeStopwatch.Elapsed.TotalSeconds);
        double avgMBps = (scanned - _options.StartOffset) / activeSeconds / (1024 * 1024);

        double instantMBps;
        lock (_snapshotLock)
        {
            double instantElapsed = _instantThroughputWatch.Elapsed.TotalSeconds;
            instantMBps = instantElapsed > 0.25 ? (scanned - _lastInstantBytes) / instantElapsed / (1024 * 1024) : 0;

            if (instantElapsed > 0.25)
            {
                _lastInstantBytes = scanned;
                _instantThroughputWatch.Restart();
            }
        }

        TimeSpan? eta = null;
        long remaining = _options.Source.Length - scanned;
        if (avgMBps > 0.01 && remaining > 0)
        {
            eta = TimeSpan.FromSeconds(remaining / (avgMBps * 1024 * 1024));
        }

        return new ProgressSnapshot(
            BytesScanned: scanned,
            TotalBytes: _options.Source.Length,
            InstantThroughputMBps: instantMBps,
            AverageThroughputMBps: avgMBps,
            Elapsed: _stopwatch.Elapsed,
            EstimatedTimeRemaining: eta,
            FilesDetected: _filesDetected,
            FilesCompleted: _filesCompleted,
            FilesFailed: _filesFailed,
            IsPaused: IsPaused,
            IsRunning: IsRunning);
    }

    private void ReaderLoop()
    {
        try
        {
            var source = _options.Source;
            int chunkSize = _options.ChunkSizeBytes;
            int lookahead = Math.Max(source.SectorSize, 64);
            int bufferSize = chunkSize + lookahead;

            long offset = _options.StartOffset;
            long sequence = offset / chunkSize;

            while (!_stopRequested && offset < source.Length)
            {
                _pauseGate.Wait();
                if (_stopRequested)
                {
                    break;
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                int nominal = (int)Math.Min(chunkSize, source.Length - offset);
                int toRead = (int)Math.Min(bufferSize, source.Length - offset);

                int read;
                try
                {
                    read = source.ReadAt(offset, buffer.AsSpan(0, toRead));
                }
                catch (Exception ex)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    Log($"Read error at offset {offset:N0}: {ex.Message}. Skipping ahead one chunk.");
                    _checkpointTracker.MarkCompleted(sequence, offset + nominal);
                    offset += nominal;
                    sequence++;
                    continue;
                }

                var chunk = new Chunk
                {
                    Sequence = sequence,
                    Offset = offset,
                    Buffer = buffer,
                    NominalLength = nominal,
                    TotalValidLength = read,
                };

                _chunkChannel.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();

                offset += nominal;
                sequence++;
            }
        }
        finally
        {
            _chunkChannel.Writer.TryComplete();
        }
    }

    private void CarveWorkerLoop()
    {
        var reader = _chunkChannel.Reader;
        var source = _options.Source;

        while (true)
        {
            Chunk chunk;
            try
            {
                chunk = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (ChannelClosedException)
            {
                break;
            }

            try
            {
                ScanChunk(source, chunk);
            }
            catch (Exception ex)
            {
                Log($"Carve worker error at offset {chunk.Offset:N0}: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
                _checkpointTracker.MarkCompleted(chunk.Sequence, chunk.EndOffset);
                Interlocked.Exchange(ref _bytesScanned, chunk.EndOffset > _bytesScanned ? chunk.EndOffset : _bytesScanned);
            }
        }
    }

    private void ScanChunk(IBlockSource source, Chunk chunk)
    {
        var span = chunk.Buffer.AsSpan(0, chunk.TotalValidLength);

        foreach (var carver in _options.Carvers)
        {
            byte[] pattern = carver.HeaderPattern;
            int searchFrom = 0;

            while (searchFrom < chunk.NominalLength)
            {
                int idx = span[searchFrom..].IndexOf(pattern);
                if (idx < 0)
                {
                    break;
                }

                int matchIndex = searchFrom + idx;
                searchFrom = matchIndex + 1;

                if (matchIndex >= chunk.NominalLength)
                {
                    break; // belongs to the next chunk's nominal region, not ours
                }

                long absolutePatternOffset = chunk.Offset + matchIndex;

                bool alreadyKnown;
                long candidateFileStart = absolutePatternOffset - carver.HeaderPatternOffsetInFile;
                lock (_knownOffsetsLock)
                {
                    alreadyKnown = _knownOffsets.Contains(candidateFileStart);
                }

                if (alreadyKnown)
                {
                    continue;
                }

                if (!carver.TryCarve(source, absolutePatternOffset, out var result))
                {
                    continue;
                }

                lock (_knownOffsetsLock)
                {
                    if (!_knownOffsets.Add(result.FileStartOffset))
                    {
                        continue;
                    }
                }

                var job = new RecoveryJob
                {
                    FileTypeId = carver.FileType.Id,
                    Extension = result.ExtensionOverride ?? carver.FileType.DefaultExtension,
                    SourceOffset = result.FileStartOffset,
                    Length = result.Length,
                    LengthIsExact = result.LengthIsExact,
                    Status = RecoveryJobStatus.Detected,
                };

                _options.Database.InsertJob(job);
                Interlocked.Increment(ref _filesDetected);
                JobUpdated?.Invoke(job);

                _jobChannel.Writer.WriteAsync(job).AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private void WriterLoop()
    {
        var reader = _jobChannel.Reader;

        while (true)
        {
            RecoveryJob job;
            try
            {
                job = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (ChannelClosedException)
            {
                break;
            }

            try
            {
                job.Status = RecoveryJobStatus.Extracting;
                _options.Database.UpdateJobStatus(job.Id, RecoveryJobStatus.Extracting);
                JobUpdated?.Invoke(job);

                RecoveryWriter.Extract(_options.Source, job, _options.OutputFolder);

                job.Status = RecoveryJobStatus.Completed;
                _options.Database.UpdateJobStatus(job.Id, RecoveryJobStatus.Completed, job.OutputPath);
                Interlocked.Increment(ref _filesCompleted);
                JobUpdated?.Invoke(job);
            }
            catch (Exception ex)
            {
                job.Status = RecoveryJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                _options.Database.UpdateJobStatus(job.Id, RecoveryJobStatus.Failed, errorMessage: ex.Message);
                Interlocked.Increment(ref _filesFailed);
                Log($"Failed to extract {job.SuggestedFileName}: {ex.Message}");
                JobUpdated?.Invoke(job);
            }
        }
    }

    private void CheckpointLoop()
    {
        while (IsRunning)
        {
            _options.Database.UpdateCheckpoint(_checkpointTracker.SafeOffset);
            Thread.Sleep(2000);
        }
    }

    private void Log(string message) => LogMessage?.Invoke(message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRequested = true;
        _pauseGate.Set();
        _pauseGate.Dispose();
    }
}

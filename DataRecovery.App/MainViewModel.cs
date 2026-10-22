using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DataRecovery.Core.Carving;
using DataRecovery.Core.Disk;
using DataRecovery.Core.Models;
using DataRecovery.Core.Persistence;
using DataRecovery.Core.Pipeline;
using Microsoft.Win32;

namespace DataRecovery.App;

public sealed class MainViewModel : ObservableObject
{
    private const string SessionFileName = "datarecovery_session.db";

    private readonly DispatcherTimer _progressTimer;
    private ScanPipeline? _pipeline;
    private RecoveryDatabase? _database;
    private IBlockSource? _source;

    public ObservableCollection<PhysicalDriveInfo> Drives { get; } = [];
    public ObservableCollection<FileTypeOption> FileTypeOptions { get; } = [];
    public ObservableCollection<RecoveryJob> Results { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    private PhysicalDriveInfo? _selectedDrive;
    public PhysicalDriveInfo? SelectedDrive
    {
        get => _selectedDrive;
        set { if (SetField(ref _selectedDrive, value)) RaiseCanExecuteChanged(); }
    }

    private string _outputFolder = "";
    public string OutputFolder
    {
        get => _outputFolder;
        set { if (SetField(ref _outputFolder, value)) RaiseCanExecuteChanged(); }
    }

    private string _statusText = "Ready. Select a source drive and output folder, then choose which file types to recover.";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private double _percentComplete;
    public double PercentComplete
    {
        get => _percentComplete;
        set => SetField(ref _percentComplete, value);
    }

    private string _throughputText = "-";
    public string ThroughputText { get => _throughputText; set => SetField(ref _throughputText, value); }

    private string _etaText = "-";
    public string EtaText { get => _etaText; set => SetField(ref _etaText, value); }

    private string _scannedText = "-";
    public string ScannedText { get => _scannedText; set => SetField(ref _scannedText, value); }

    private string _filesText = "-";
    public string FilesText { get => _filesText; set => SetField(ref _filesText, value); }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { if (SetField(ref _isRunning, value)) RaiseCanExecuteChanged(); }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set { if (SetField(ref _isPaused, value)) { OnPropertyChanged(nameof(PauseResumeLabel)); RaiseCanExecuteChanged(); } }
    }

    public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";

    public ICommand RefreshDrivesCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public AsyncRelayCommand StopCommand { get; }

    public MainViewModel()
    {
        foreach (var ft in FileTypeDefinition.All)
        {
            FileTypeOptions.Add(new FileTypeOption(ft));
        }

        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && SelectedDrive != null && !string.IsNullOrWhiteSpace(OutputFolder));
        PauseResumeCommand = new RelayCommand(TogglePause, () => IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning);

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        RefreshDrives();
    }

    private void RaiseCanExecuteChanged()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        (PauseResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshDrives()
    {
        var selectedPath = SelectedDrive?.DevicePath;
        Drives.Clear();
        try
        {
            foreach (var drive in DiskEnumerator.ListPhysicalDrives())
            {
                Drives.Add(drive);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to enumerate drives: {ex.Message}");
        }

        SelectedDrive = Drives.FirstOrDefault(d => d.DevicePath == selectedPath) ?? Drives.FirstOrDefault();
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose an output folder on a DIFFERENT drive than the one being recovered",
        };

        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
            CheckForResumableSession();
        }
    }

    private void CheckForResumableSession()
    {
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
        {
            return;
        }

        string dbPath = Path.Combine(OutputFolder, SessionFileName);
        if (!File.Exists(dbPath))
        {
            return;
        }

        try
        {
            using var db = new RecoveryDatabase(dbPath);
            var session = db.LoadSession();
            if (session is null)
            {
                return;
            }

            double percent = session.SourceSizeBytes > 0
                ? session.LastCheckpointOffset * 100.0 / session.SourceSizeBytes
                : 0;

            StatusText = $"Found a previous session ({percent:0.#}% scanned) for {session.SourceDevicePath} in this folder. " +
                          "Click Start to resume it, or pick an empty folder to start fresh.";

            var matchingDrive = Drives.FirstOrDefault(d => d.DevicePath == session.SourceDevicePath);
            if (matchingDrive != null)
            {
                SelectedDrive = matchingDrive;
            }

            var selectedIds = new HashSet<string>(session.SelectedFileTypeIds, StringComparer.OrdinalIgnoreCase);
            foreach (var option in FileTypeOptions)
            {
                option.IsSelected = selectedIds.Contains(option.Definition.Id);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read previous session: {ex.Message}");
        }
    }

    private bool ValidateOutputIsOnDifferentDrive()
    {
        if (SelectedDrive is null)
        {
            return false;
        }

        string? root = Path.GetPathRoot(Path.GetFullPath(OutputFolder))?.TrimEnd('\\');
        if (string.IsNullOrEmpty(root))
        {
            return true; // can't determine - don't block, just proceed
        }

        string letter = root.EndsWith(':') ? root : root + ":";
        if (SelectedDrive.VolumeLetters.Any(v => string.Equals(v.TrimEnd('\\'), letter, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                "The output folder appears to be on the same physical drive you're recovering from.\n\n" +
                "Writing recovered files back to the source drive can overwrite data you're trying to recover. " +
                "Please choose an output folder on a different physical drive.",
                "Unsafe output location",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private async Task StartAsync()
    {
        await Task.Yield(); // keep the command's initial dispatch off the UI call stack

        if (SelectedDrive is null || string.IsNullOrWhiteSpace(OutputFolder))
        {
            return;
        }

        var selectedTypes = FileTypeOptions.Where(o => o.IsSelected).ToList();
        if (selectedTypes.Count == 0)
        {
            MessageBox.Show("Select at least one file type to recover.", "Nothing to do", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(OutputFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create/access output folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!ValidateOutputIsOnDifferentDrive())
        {
            return;
        }

        // Any database/source from a previous scan in this app session (now stopped) must be
        // released before we potentially point at a different drive/output folder.
        _database?.Dispose();
        _database = null;
        _source?.Dispose();
        _source = null;

        string dbPath = Path.Combine(OutputFolder, SessionFileName);
        long startOffset = 0;
        bool isResume = false;
        RecoveryDatabase? dbToUse = null;

        if (File.Exists(dbPath))
        {
            var db = new RecoveryDatabase(dbPath);
            var session = db.LoadSession();
            if (session != null && session.SourceDevicePath == SelectedDrive.DevicePath)
            {
                double percent = session.SourceSizeBytes > 0 ? session.LastCheckpointOffset * 100.0 / session.SourceSizeBytes : 0;
                var choice = MessageBox.Show(
                    $"A previous session for this drive is {percent:0.#}% complete.\n\nResume where it left off?",
                    "Resume previous session?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (choice == MessageBoxResult.Yes)
                {
                    startOffset = session.LastCheckpointOffset;
                    isResume = true;
                    dbToUse = db;
                }
                else
                {
                    db.Dispose();
                    File.Delete(dbPath);
                }
            }
            else
            {
                var choice = MessageBox.Show(
                    "A previous session for a DIFFERENT drive exists in this output folder.\n\n" +
                    "Continuing will discard that session's records. Continue?",
                    "Different session found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                db.Dispose();
                if (choice != MessageBoxResult.Yes)
                {
                    return;
                }

                File.Delete(dbPath);
            }
        }

        _database = dbToUse ?? new RecoveryDatabase(dbPath);

        if (!isResume)
        {
            Results.Clear();
        }
        else
        {
            foreach (var job in _database.GetJobsByStatuses())
            {
                Results.Add(job);
            }
        }

        IBlockSource source;
        try
        {
            source = new PhysicalDriveBlockSource(SelectedDrive.DevicePath);
            _source = source;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open {SelectedDrive.DevicePath}:\n{ex.Message}\n\n" +
                "Make sure this app is running as Administrator.",
                "Cannot open drive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _database.SaveSession(new ScanSession
        {
            SourceDevicePath = SelectedDrive.DevicePath,
            SourceSizeBytes = source.Length,
            OutputFolder = OutputFolder,
            SelectedFileTypeIds = selectedTypes.Select(t => t.Definition.Id).ToList(),
            LastCheckpointOffset = startOffset,
        });

        var carvers = CarverRegistry.GetEnabled(selectedTypes.Select(t => t.Definition.Id));

        var options = new ScanPipelineOptions
        {
            Source = source,
            SourceDevicePath = SelectedDrive.DevicePath,
            OutputFolder = OutputFolder,
            Carvers = carvers,
            Database = _database,
            StartOffset = startOffset,
        };

        _pipeline = new ScanPipeline(options);
        _pipeline.LogMessage += msg => Application.Current.Dispatcher.BeginInvoke(() => AppendLog(msg));
        _pipeline.JobUpdated += job => Application.Current.Dispatcher.BeginInvoke(() => UpsertResult(job));

        _pipeline.Start();
        IsRunning = true;
        IsPaused = false;
        StatusText = isResume ? "Resuming previous scan..." : "Scanning...";
        _progressTimer.Start();
    }

    private void TogglePause()
    {
        if (_pipeline is null)
        {
            return;
        }

        if (_pipeline.IsPaused)
        {
            _pipeline.Resume();
        }
        else
        {
            _pipeline.Pause();
        }

        IsPaused = _pipeline.IsPaused;
        StatusText = IsPaused ? "Paused." : "Scanning...";
    }

    public async Task StopAsync()
    {
        if (_pipeline is null)
        {
            return;
        }

        StatusText = "Stopping (finishing in-flight work and saving checkpoint)...";
        await _pipeline.StopAsync();
        _progressTimer.Stop();
        IsRunning = false;
        IsPaused = false;
        StatusText = "Stopped. Progress has been saved - you can resume later from this output folder.";
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_pipeline is null)
        {
            return;
        }

        var snap = _pipeline.GetSnapshot();
        PercentComplete = snap.PercentComplete;
        ThroughputText = $"{snap.InstantThroughputMBps:0.#} MB/s (avg {snap.AverageThroughputMBps:0.#} MB/s)";
        EtaText = snap.EstimatedTimeRemaining is { } eta ? FormatTimeSpan(eta) : "calculating...";
        ScannedText = $"{FormatBytes(snap.BytesScanned)} / {FormatBytes(snap.TotalBytes)} ({snap.PercentComplete:0.##}%) - elapsed {FormatTimeSpan(snap.Elapsed)}";
        FilesText = $"Detected: {snap.FilesDetected}   Completed: {snap.FilesCompleted}   Failed: {snap.FilesFailed}";

        if (!snap.IsRunning && IsRunning)
        {
            // The scan finished on its own (whole drive scanned) rather than via the Stop button.
            IsRunning = false;
            IsPaused = false;
            _progressTimer.Stop();
            StatusText = "Scan complete - the entire drive has been scanned. Recovered files are in the output folder.";
        }
    }

    private void UpsertResult(RecoveryJob job)
    {
        for (int i = 0; i < Results.Count; i++)
        {
            if (Results[i].Id == job.Id)
            {
                Results[i] = job;
                return;
            }
        }

        Results.Insert(0, job);
    }

    private void AppendLog(string message)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogLines.Count > 500)
        {
            LogLines.RemoveAt(0);
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024;
        return bytes switch
        {
            _ when bytes >= (long)tb => $"{bytes / tb:0.##} TB",
            _ when bytes >= (long)gb => $"{bytes / gb:0.##} GB",
            _ when bytes >= (long)mb => $"{bytes / mb:0.#} MB",
            _ => $"{bytes / kb:0.#} KB",
        };
    }

    private static string FormatTimeSpan(TimeSpan ts) =>
        ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s" : $"{ts.Minutes}m {ts.Seconds}s";
}

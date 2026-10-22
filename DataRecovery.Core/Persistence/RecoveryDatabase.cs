using System.Text;
using DataRecovery.Core.Models;
using Microsoft.Data.Sqlite;

namespace DataRecovery.Core.Persistence;

/// <summary>
/// SQLite-backed (WAL mode) checkpoint and found-files database. This is what makes the scan
/// resumable: the scan-progress offset and every carve candidate's status are committed here
/// continuously, so killing the app (or losing power) loses at most a few seconds of work.
///
/// A single shared connection is used, guarded by a lock - SQLite serializes writes internally
/// anyway, and this database is never the throughput bottleneck (the disk scan is).
/// </summary>
public sealed class RecoveryDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public string DatabasePath { get; }

    public RecoveryDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Session (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                SourceDevicePath TEXT NOT NULL,
                SourceSizeBytes INTEGER NOT NULL,
                OutputFolder TEXT NOT NULL,
                SelectedFileTypeIds TEXT NOT NULL,
                LastCheckpointOffset INTEGER NOT NULL,
                CreatedUtc TEXT NOT NULL,
                LastUpdatedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS FoundFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FileTypeId TEXT NOT NULL,
                Extension TEXT NOT NULL,
                SourceOffset INTEGER NOT NULL,
                Length INTEGER NOT NULL,
                LengthIsExact INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                OutputPath TEXT,
                ErrorMessage TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_FoundFiles_Status ON FoundFiles(Status);
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveSession(ScanSession session)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Session (Id, SourceDevicePath, SourceSizeBytes, OutputFolder, SelectedFileTypeIds, LastCheckpointOffset, CreatedUtc, LastUpdatedUtc)
                VALUES (1, $source, $size, $output, $types, $checkpoint, $created, $updated)
                ON CONFLICT(Id) DO UPDATE SET
                    SourceDevicePath = $source,
                    SourceSizeBytes = $size,
                    OutputFolder = $output,
                    SelectedFileTypeIds = $types,
                    LastCheckpointOffset = $checkpoint,
                    LastUpdatedUtc = $updated;
                """;
            cmd.Parameters.AddWithValue("$source", session.SourceDevicePath);
            cmd.Parameters.AddWithValue("$size", session.SourceSizeBytes);
            cmd.Parameters.AddWithValue("$output", session.OutputFolder);
            cmd.Parameters.AddWithValue("$types", string.Join(",", session.SelectedFileTypeIds));
            cmd.Parameters.AddWithValue("$checkpoint", session.LastCheckpointOffset);
            cmd.Parameters.AddWithValue("$created", session.CreatedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$updated", session.LastUpdatedUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateCheckpoint(long offset)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Session SET LastCheckpointOffset = $offset, LastUpdatedUtc = $updated WHERE Id = 1;";
            cmd.Parameters.AddWithValue("$offset", offset);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public ScanSession? LoadSession()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT SourceDevicePath, SourceSizeBytes, OutputFolder, SelectedFileTypeIds, LastCheckpointOffset, CreatedUtc, LastUpdatedUtc FROM Session WHERE Id = 1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ScanSession
            {
                SourceDevicePath = reader.GetString(0),
                SourceSizeBytes = reader.GetInt64(1),
                OutputFolder = reader.GetString(2),
                SelectedFileTypeIds = reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries),
                LastCheckpointOffset = reader.GetInt64(4),
                CreatedUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
                LastUpdatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
            };
        }
    }

    public long InsertJob(RecoveryJob job)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO FoundFiles (FileTypeId, Extension, SourceOffset, Length, LengthIsExact, Status, OutputPath, ErrorMessage)
                VALUES ($type, $ext, $offset, $length, $exact, $status, $path, $error);
                """;
            cmd.Parameters.AddWithValue("$type", job.FileTypeId);
            cmd.Parameters.AddWithValue("$ext", job.Extension);
            cmd.Parameters.AddWithValue("$offset", job.SourceOffset);
            cmd.Parameters.AddWithValue("$length", job.Length);
            cmd.Parameters.AddWithValue("$exact", job.LengthIsExact ? 1 : 0);
            cmd.Parameters.AddWithValue("$status", (int)job.Status);
            cmd.Parameters.AddWithValue("$path", job.OutputPath ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$error", job.ErrorMessage ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();

            using var idCmd = _connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            long id = (long)idCmd.ExecuteScalar()!;
            job.Id = id;
            return id;
        }
    }

    public void UpdateJobStatus(long id, RecoveryJobStatus status, string? outputPath = null, string? errorMessage = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE FoundFiles SET Status = $status, OutputPath = $path, ErrorMessage = $error WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$status", (int)status);
            cmd.Parameters.AddWithValue("$path", outputPath ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$error", errorMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<RecoveryJob> GetJobsByStatuses(params RecoveryJobStatus[] statuses)
    {
        lock (_gate)
        {
            var sb = new StringBuilder("SELECT Id, FileTypeId, Extension, SourceOffset, Length, LengthIsExact, Status, OutputPath, ErrorMessage FROM FoundFiles");
            if (statuses.Length > 0)
            {
                sb.Append(" WHERE Status IN (").Append(string.Join(",", statuses.Select(s => (int)s))).Append(')');
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            using var reader = cmd.ExecuteReader();

            var results = new List<RecoveryJob>();
            while (reader.Read())
            {
                results.Add(new RecoveryJob
                {
                    Id = reader.GetInt64(0),
                    FileTypeId = reader.GetString(1),
                    Extension = reader.GetString(2),
                    SourceOffset = reader.GetInt64(3),
                    Length = reader.GetInt64(4),
                    LengthIsExact = reader.GetInt32(5) != 0,
                    Status = (RecoveryJobStatus)reader.GetInt32(6),
                    OutputPath = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                });
            }

            return results;
        }
    }

    public (int Detected, int Extracting, int Completed, int Failed) GetStatusCounts()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Status, COUNT(*) FROM FoundFiles GROUP BY Status;";
            using var reader = cmd.ExecuteReader();

            int detected = 0, extracting = 0, completed = 0, failed = 0;
            while (reader.Read())
            {
                int count = reader.GetInt32(1);
                switch ((RecoveryJobStatus)reader.GetInt32(0))
                {
                    case RecoveryJobStatus.Detected: detected = count; break;
                    case RecoveryJobStatus.Extracting: extracting = count; break;
                    case RecoveryJobStatus.Completed: completed = count; break;
                    case RecoveryJobStatus.Failed: failed = count; break;
                }
            }

            return (detected, extracting, completed, failed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}

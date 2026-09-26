using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IsTranscribe.Application.Diagnostics;

public sealed class BootstrapFileLogger
{
    private const long MaxActiveFileBytes = 10 * 1024 * 1024;
    private const int MaxRotatedFiles = 20;
    private const long MaxDiagnosticsFootprintBytes = 200 * 1024 * 1024;
    private static readonly TimeSpan MaxRetention = TimeSpan.FromDays(14);

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _directoryPath;
    private readonly string _baseName;
    private readonly string _extension;

    public BootstrapFileLogger(string filePath)
    {
        _filePath = filePath;
        _directoryPath = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("A log file path must include a directory.", nameof(filePath));
        _baseName = Path.GetFileNameWithoutExtension(filePath);
        _extension = Path.GetExtension(filePath);
        Directory.CreateDirectory(_directoryPath);
    }

    public string FilePath => _filePath;

    public void Info(string message) => Write("Info", "HOST_INFO", message);

    public void Warning(string message) => Write("Warning", "HOST_WARNING", message);

    public void Error(string message) => Write("Error", "HOST_ERROR", message);

    public void Error(Exception exception, string message) =>
        Write(
            "Error",
            "HOST_EXCEPTION",
            message,
            metadata: new Dictionary<string, object?>
            {
                ["exception"] = exception.ToString()
            });

    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#diagnostics.events
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#diagnostics.rotation
    public void LogEvent(
        string level,
        string eventCode,
        string message,
        string? sessionId = null,
        string? jobName = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => Write(level, eventCode, message, sessionId, jobName, metadata);

    private void Write(
        string level,
        string eventCode,
        string message,
        string? sessionId = null,
        string? jobName = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var entry = new HostLogEntry
        {
            TimestampUtc = timestamp,
            Level = level,
            EventCode = eventCode,
            SessionId = sessionId,
            JobName = jobName,
            Message = message,
            Metadata = metadata is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(metadata)
        };

        var line = JsonSerializer.Serialize(entry);
        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
                CleanupRetentionAndFootprint();
            }
            catch (IOException)
            {
                // Diagnostics must never interrupt a runtime path when another
                // process temporarily opens the local log without write sharing.
            }
            catch (UnauthorizedAccessException)
            {
                // A transient filesystem policy change is a degraded logging
                // condition, not a reason to stop capture or detection.
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_filePath);
        if (!info.Exists || info.Length < MaxActiveFileBytes)
        {
            return;
        }

        var oldestPath = BuildRotatedPath(MaxRotatedFiles);
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (var index = MaxRotatedFiles - 1; index >= 1; index--)
        {
            var sourcePath = BuildRotatedPath(index);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var destinationPath = BuildRotatedPath(index + 1);
            File.Move(sourcePath, destinationPath, overwrite: true);
        }

        File.Move(_filePath, BuildRotatedPath(1), overwrite: true);
    }

    private void CleanupRetentionAndFootprint()
    {
        var now = DateTimeOffset.UtcNow;
        var files = EnumerateManagedLogFiles();

        // Remove expired files and track survivors in a single pass.
        var survivors = new List<FileInfo>(files.Count);
        foreach (var file in files)
        {
            if (now - file.LastWriteTimeUtc > MaxRetention)
            {
                TryDelete(file.FullName);
            }
            else
            {
                survivors.Add(file);
            }
        }

        var totalBytes = survivors.Sum(file => file.Length);
        if (totalBytes <= MaxDiagnosticsFootprintBytes)
        {
            return;
        }

        foreach (var file in survivors.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (totalBytes <= MaxDiagnosticsFootprintBytes)
            {
                break;
            }

            totalBytes -= file.Length;
            TryDelete(file.FullName);
        }
    }

    private List<FileInfo> EnumerateManagedLogFiles()
        => Directory.EnumerateFiles(_directoryPath, $"{_baseName}*{_extension}", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .ToList();

    private string BuildRotatedPath(int index)
        => Path.Combine(_directoryPath, $"{_baseName}.{index}{_extension}");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Diagnostics cleanup should never crash the host path.
        }
    }

    private sealed record HostLogEntry
    {
        [JsonPropertyName("timestamp_utc")]
        public DateTimeOffset TimestampUtc { get; init; }

        [JsonPropertyName("level")]
        public string Level { get; init; } = string.Empty;

        [JsonPropertyName("event_code")]
        public string EventCode { get; init; } = string.Empty;

        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("job_name")]
        public string? JobName { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("metadata")]
        public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
    }
}

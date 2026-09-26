using System.Globalization;
using System.Text.RegularExpressions;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Persistence;

public sealed class ArtifactPathResolver(LocalAppPaths paths)
{
    private static readonly Regex DuplicateWhitespace = new(@"\s+", RegexOptions.Compiled);
    private readonly LocalAppPaths _paths = paths;

    public string GetRecordingsRoot(ApplicationSettings settings)
    {
        var configuredPath = settings.ReleaseV2?.RecordingsFolder;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = settings.Storage.RecordingsFolder;
        }

        return string.IsNullOrWhiteSpace(configuredPath)
            ? _paths.DefaultRecordingsDirectory
            : configuredPath;
    }

    public string GetTranscriptsRoot(ApplicationSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Storage.TranscriptsFolder)
            ? _paths.DefaultTranscriptsDirectory
            : settings.Storage.TranscriptsFolder;

    public string GetFailedTempRoot(ApplicationSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Storage.FailedTempFolder)
            ? _paths.TempDirectory
            : settings.Storage.FailedTempFolder;

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#force-record.bootstrap
    public string GetTempSessionDirectoryPath(ApplicationSettings settings, Guid sessionId) =>
        Path.Combine(
            GetFailedTempRoot(settings),
            "sessions",
            sessionId.ToString("N").ToLowerInvariant());

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#file-layout.session-dir
    public string GetSessionDirectoryPath(ApplicationSettings settings, Guid sessionId, DateTimeOffset createdAtUtc)
    {
        var utcTimestamp = createdAtUtc.UtcDateTime;
        return Path.Combine(
            GetRecordingsRoot(settings),
            utcTimestamp.ToString("yyyy", CultureInfo.InvariantCulture),
            utcTimestamp.ToString("MM", CultureInfo.InvariantCulture),
            sessionId.ToString("N").ToLowerInvariant());
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#storage
    // @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.format
    public string GetPrimaryAudioFilePath(
        ApplicationSettings settings,
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string sourceApp)
    {
        var localTimestamp = createdAtUtc.ToLocalTime();
        var collisionToken = sessionId.ToString("N")[..8].ToLowerInvariant();
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{localTimestamp:yyyy-MM-dd HH-mm} — {SanitizeSegment(sourceApp)} — {collisionToken}.mp3");
        return Path.Combine(
            GetSessionDirectoryPath(settings, sessionId, createdAtUtc),
            fileName);
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    // @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#finalization
    public string GetStagedPrimaryAudioFilePath(
        ApplicationSettings settings,
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string sourceApp)
    {
        var finalPath = GetPrimaryAudioFilePath(settings, sessionId, createdAtUtc, sourceApp);
        return Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{Path.GetFileName(finalPath)}.partial");
    }

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#file-layout.filename-template
    public string GetTranscriptFilePath(
        ApplicationSettings settings,
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string sourceApp,
        string mode,
        string extension)
    {
        var fileName = settings.Storage.FilenameTemplate
            .Replace("{SessionId}", sessionId.ToString("N").ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("{SourceApp}", SanitizeSegment(sourceApp), StringComparison.Ordinal)
            .Replace("{Date}", createdAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Time}", createdAtUtc.ToString("HH-mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Mode}", SanitizeSegment(mode), StringComparison.Ordinal);

        var targetRoot = GetTranscriptsRoot(settings);
        if (PathsAreEquivalent(targetRoot, GetRecordingsRoot(settings)))
        {
            return Path.Combine(
                GetSessionDirectoryPath(settings, sessionId, createdAtUtc),
                $"{fileName}.{extension.TrimStart('.')}");
        }

        return Path.Combine(targetRoot, $"{fileName}.{extension.TrimStart('.')}");
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
    public string GetCanonicalTranscriptFilePath(
        ApplicationSettings settings,
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string extension)
    {
        var normalizedExtension = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (normalizedExtension is not ("md" or "json"))
        {
            throw new ArgumentException("A canonical transcript must use md or json.", nameof(extension));
        }

        return Path.Combine(
            GetSessionDirectoryPath(settings, sessionId, createdAtUtc),
            $"{sessionId:N}.transcript.{normalizedExtension}");
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public string GetStagedTranscriptFilePath(
        ApplicationSettings settings,
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string jobId,
        string extension)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A transcription job id is required.", nameof(jobId));
        }

        var normalizedJobId = jobId.Trim().ToLowerInvariant();
        if (normalizedJobId.Length > 128
            || normalizedJobId.Any(static character =>
                !(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
        {
            throw new ArgumentException("The transcription job id is invalid.", nameof(jobId));
        }

        var finalPath = GetCanonicalTranscriptFilePath(settings, sessionId, createdAtUtc, extension);
        return Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{Path.GetFileName(finalPath)}.{normalizedJobId}.partial");
    }

    private static bool PathsAreEquivalent(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var sanitized = string.Join(
            "_",
            value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        sanitized = DuplicateWhitespace.Replace(sanitized, " ").Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }
}

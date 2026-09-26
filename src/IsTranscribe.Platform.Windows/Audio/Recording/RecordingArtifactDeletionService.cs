using IsTranscribe.Application.Platform;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Platform.Windows.Audio.Recording;

/// <summary>
/// Applies the destructive filesystem half of a repository-checkpointed recording removal.
/// Every target must be an explicit recording path under a configured app-owned root.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.compatibility
/// </remarks>
internal sealed class RecordingArtifactDeletionService(
    LocalAppPaths paths,
    ArtifactPathResolver pathResolver,
    Func<ApplicationSettings> settingsAccessor) : IRecordingArtifactDeletionService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".opus",
        ".wav",
        ".wma"
    };

    private readonly LocalAppPaths _paths = paths;
    private readonly ArtifactPathResolver _pathResolver = pathResolver;
    private readonly Func<ApplicationSettings> _settingsAccessor = settingsAccessor;

    public void DeleteActiveSession(Guid sessionId, string tempSessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempSessionPath);
        var expected = NormalizeDirectory(_pathResolver.GetTempSessionDirectoryPath(
            _settingsAccessor(),
            sessionId));
        var requested = NormalizeDirectory(tempSessionPath);
        if (!string.Equals(expected, requested, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The active recording temporary directory is outside its canonical app-owned path.");
        }

        ValidateDirectoryTreeDoesNotCrossReparsePoints(requested);
        DeleteDirectoryWithoutFollowingReparsePoints(requested);
    }

    public void DeleteRecentArtifacts(RecentRecordingRemovalWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (!Guid.TryParseExact(workItem.Id, "N", out var sessionId))
        {
            throw new InvalidDataException("The recording session id is invalid.");
        }

        var settings = _settingsAccessor();
        var recordingRoots = NormalizeDistinctRoots(
            _pathResolver.GetRecordingsRoot(settings),
            _paths.DefaultRecordingsDirectory);
        var tempRoots = NormalizeDistinctRoots(
            _pathResolver.GetFailedTempRoot(settings),
            _paths.TempDirectory);
        var allRoots = recordingRoots.Concat(tempRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var normalizedTempSession = ValidateRecentTempSession(
            workItem.TempSessionPath,
            tempRoots,
            sessionId);
        var explicitFiles = new[]
            {
                workItem.PrimaryAudioPath,
                workItem.AudioMixPath,
                workItem.AudioOutputPath,
                workItem.AudioMicPath,
                workItem.StagedPrimaryPath
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var validatedFiles = new List<(string Path, string Root)>(explicitFiles.Length);
        foreach (var path in explicitFiles)
        {
            if (!IsRecordingArtifactFileName(path))
            {
                throw new InvalidOperationException(
                    $"The requested recording artifact has an unsupported file type: {Path.GetFileName(path)}");
            }

            var ownerRoot = FindOwningRoot(path, allRoots, sessionId)
                ?? throw new InvalidOperationException(
                    "A requested recording artifact is outside configured app-owned roots.");
            ValidatePathDoesNotCrossReparsePoints(ownerRoot, path);
            validatedFiles.Add((path, ownerRoot));
        }

        if (!string.IsNullOrWhiteSpace(workItem.SourceManifestPath))
        {
            var manifestPath = Path.GetFullPath(workItem.SourceManifestPath);
            var manifestName = Path.GetFileName(manifestPath);
            if (normalizedTempSession is null
                || !IsStrictDescendant(normalizedTempSession, manifestPath)
                || manifestName is not ("source-manifest.json" or "source-manifest.json.partial"))
            {
                throw new InvalidOperationException(
                    "The source manifest is outside the canonical temporary recording directory.");
            }

            ValidatePathDoesNotCrossReparsePoints(normalizedTempSession, manifestPath);
        }

        // Validation is deliberately complete before the first mutation so an unsafe path
        // cannot leave a partially deleted recording.
        foreach (var (path, _) in validatedFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(workItem.SourceManifestPath)
            && File.Exists(workItem.SourceManifestPath))
        {
            File.Delete(workItem.SourceManifestPath);
        }

        if (normalizedTempSession is not null)
        {
            DeleteDirectoryWithoutFollowingReparsePoints(normalizedTempSession);
        }

        foreach (var directory in validatedFiles
                     .Select(static item => Path.GetDirectoryName(item.Path))
                     .Where(static directory => !string.IsNullOrWhiteSpace(directory))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalizedDirectory = NormalizeDirectory(directory!);
            if (string.Equals(
                    Path.GetFileName(normalizedDirectory),
                    sessionId.ToString("N"),
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(normalizedDirectory)
                && !Directory.EnumerateFileSystemEntries(normalizedDirectory).Any())
            {
                Directory.Delete(normalizedDirectory, recursive: false);
            }
        }
    }

    private static string? ValidateRecentTempSession(
        string? tempSessionPath,
        IReadOnlyList<string> tempRoots,
        Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(tempSessionPath))
        {
            return null;
        }

        var requested = NormalizeDirectory(tempSessionPath);
        var sessionKey = sessionId.ToString("N");
        var isCanonical = tempRoots.Any(root => string.Equals(
                NormalizeDirectory(Path.Combine(root, "sessions", sessionKey)),
                requested,
                StringComparison.OrdinalIgnoreCase))
            || string.Equals(Path.GetFileName(requested), sessionKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFileName(Path.GetDirectoryName(requested)),
                "sessions",
                StringComparison.OrdinalIgnoreCase);
        if (!isCanonical)
        {
            throw new InvalidOperationException(
                "The temporary recording directory is outside its canonical app-owned path.");
        }

        ValidateDirectoryTreeDoesNotCrossReparsePoints(requested);
        return requested;
    }

    private static IReadOnlyList<string> NormalizeDistinctRoots(params string[] roots) =>
        roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static root => root.Length)
            .ToArray();

    private static string? FindOwningRoot(
        string path,
        IReadOnlyList<string> roots,
        Guid sessionId)
    {
        var configuredRoot = roots.FirstOrDefault(root => IsStrictDescendant(root, path));
        if (configuredRoot is not null)
        {
            return configuredRoot;
        }

        // Folder settings affect new sessions without moving existing artifacts. A persisted
        // v2 session directory or legacy filename carrying the full session id remains an
        // app-owned target even after its former custom root is no longer configured.
        var sessionKey = sessionId.ToString("N");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (string.Equals(
                    Path.GetFileName(NormalizeDirectory(directory)),
                    sessionKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeDirectory(directory);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return Path.GetFileName(path).Contains(sessionKey, StringComparison.OrdinalIgnoreCase)
            ? NormalizeDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!)
            : null;
    }

    private static bool IsRecordingArtifactFileName(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        if (AudioExtensions.Contains(extension))
        {
            return true;
        }

        return string.Equals(extension, ".partial", StringComparison.OrdinalIgnoreCase)
               && AudioExtensions.Any(audioExtension => fileName.EndsWith(
                   $"{audioExtension}.partial",
                   StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStrictDescendant(string root, string path)
    {
        var normalizedRoot = NormalizeDirectory(root);
        var normalizedPath = Path.GetFullPath(path);
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void ValidatePathDoesNotCrossReparsePoints(string ownerRoot, string path)
    {
        var normalizedRoot = NormalizeDirectory(ownerRoot);
        if (Directory.Exists(normalizedRoot)
            && File.GetAttributes(normalizedRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("An app-owned recording root cannot be a reparse point.");
        }

        var current = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrWhiteSpace(current)
               && !string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current)
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "A recording artifact path crosses a filesystem reparse point.");
            }

            current = Path.GetDirectoryName(current);
        }

        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("A recording artifact cannot be a reparse point.");
        }
    }

    private static void ValidateDirectoryTreeDoesNotCrossReparsePoints(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "A recording directory cannot be deleted through a filesystem reparse point.");
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            if (File.GetAttributes(childDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "A recording directory contains a filesystem reparse point.");
            }

            ValidateDirectoryTreeDoesNotCrossReparsePoints(childDirectory);
        }
    }

    internal static void DeleteDirectoryWithoutFollowingReparsePoints(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        ValidateDirectoryTreeDoesNotCrossReparsePoints(directory);
        Directory.Delete(directory, recursive: true);
    }
}

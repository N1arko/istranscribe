using IsTranscribe.Application.Platform;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Deletes only repository-checkpointed audio paths inside app-owned recording roots.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#recording
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.compatibility
/// </remarks>
internal sealed class MacOSRecordingArtifactDeletionService(
    LocalAppPaths paths,
    ArtifactPathResolver pathResolver,
    Func<ApplicationSettings> settingsAccessor) : IRecordingArtifactDeletionService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav"
    };

    public void DeleteRecentArtifacts(RecentRecordingRemovalWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (!Guid.TryParseExact(workItem.Id, "N", out var sessionId))
        {
            throw new InvalidDataException("Recording session id is invalid.");
        }

        var settings = settingsAccessor();
        var roots = new[]
        {
            pathResolver.GetRecordingsRoot(settings),
            paths.DefaultRecordingsDirectory,
            pathResolver.GetFailedTempRoot(settings),
            paths.TempDirectory
        }.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var files = new[]
        {
            workItem.PrimaryAudioPath,
            workItem.AudioMixPath,
            workItem.AudioOutputPath,
            workItem.AudioMicPath,
            workItem.StagedPrimaryPath
        }.Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var extension = Path.GetExtension(fileName);
            var validExtension = AudioExtensions.Contains(extension)
                                 || extension.Equals(".partial", StringComparison.OrdinalIgnoreCase)
                                 && AudioExtensions.Any(audio => fileName.EndsWith(audio + ".partial", StringComparison.OrdinalIgnoreCase));
            if (!validExtension || !IsOwned(file, roots, sessionId))
            {
                throw new InvalidOperationException("Recording artifact is outside app-owned roots or has an unsupported type.");
            }

            EnsureNoSymbolicLink(file, roots);
        }

        var temp = ValidateTemp(workItem.TempSessionPath, roots, sessionId);
        foreach (var file in files)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        if (temp is not null && Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string? ValidateTemp(string? path, IReadOnlyList<string> roots, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = Normalize(path);
        if (!string.Equals(Path.GetFileName(normalized), sessionId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(normalized)), "sessions", StringComparison.OrdinalIgnoreCase)
            || !roots.Any(root => IsDescendant(root, normalized)))
        {
            throw new InvalidOperationException("Temporary recording directory is outside its canonical location.");
        }

        EnsureNoSymbolicLink(normalized, roots);
        return normalized;
    }

    private static bool IsOwned(string path, IReadOnlyList<string> roots, Guid sessionId) =>
        roots.Any(root => IsDescendant(root, path))
        || Path.GetDirectoryName(path)?.Split(Path.DirectorySeparatorChar).Any(segment =>
            segment.Equals(sessionId.ToString("N"), StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsDescendant(string root, string path)
    {
        var prefix = Normalize(root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.Ordinal);
    }

    private static void EnsureNoSymbolicLink(string path, IReadOnlyList<string> roots)
    {
        var fullPath = Path.GetFullPath(path);
        var root = roots.Where(candidate => IsDescendant(candidate, fullPath))
            .OrderByDescending(static candidate => candidate.Length)
            .FirstOrDefault();
        if (root is null)
        {
            return;
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Recording path crosses a symbolic link.");
            }
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

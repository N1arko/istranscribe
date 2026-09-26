using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using IsTranscribe.Core.Audio;

namespace IsTranscribe.Platform.Windows.Audio.Finalization;

/// <summary>
/// Persists the closed capture legs and their session-relative timeline before encoding starts.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
internal static class RecordingSourceManifestStore
{
    private const int CurrentSchemaVersion = 1;
    private const string ManifestFileName = "source-manifest.json";
    internal const string ContinuityLegCheckpointFileName = "continuity-leg.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static async Task<string> WriteAsync(
        string tempSessionDirectory,
        Guid sessionId,
        IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempSessionDirectory);
        ArgumentNullException.ThrowIfNull(artifacts);
        Directory.CreateDirectory(tempSessionDirectory);

        var manifestPath = Path.Combine(tempSessionDirectory, ManifestFileName);
        var partialPath = $"{manifestPath}.partial";
        var manifest = new RecordingSourceManifest(
            CurrentSchemaVersion,
            sessionId,
            DateTimeOffset.UtcNow,
            artifacts.Select(static artifact => new RecordingSourceManifestEntry(
                artifact.Kind,
                artifact.Path,
                artifact.BytesWritten,
                artifact.FinalizedAtUtc,
                artifact.RelativeStartOffset)).ToArray());

        try
        {
            await using (var stream = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(partialPath, manifestPath, overwrite: true);
            return manifestPath;
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    public static IReadOnlyList<AudioCaptureArtifactSnapshot> Read(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return [];
        }

        try
        {
            using var stream = File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifest = JsonSerializer.Deserialize<RecordingSourceManifest>(stream, SerializerOptions);
            if (manifest is null || manifest.SchemaVersion != CurrentSchemaVersion)
            {
                return [];
            }

            return manifest.Artifacts
                .Where(static artifact => !string.IsNullOrWhiteSpace(artifact.Path))
                .Select(static artifact => new AudioCaptureArtifactSnapshot(
                    artifact.Kind,
                    artifact.Path,
                    artifact.BytesWritten,
                    artifact.FinalizedAtUtc,
                    artifact.RelativeStartOffset))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public static async Task<string> WriteContinuityLegCheckpointAsync(
        string legDirectory,
        Guid sessionId,
        int legIndex,
        TimeSpan relativeStartOffset,
        IReadOnlyCollection<AudioCaptureArtifactKind> artifactKinds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legDirectory);
        ArgumentNullException.ThrowIfNull(artifactKinds);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        if (legIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(legIndex), legIndex, "Continuity leg index must be positive.");
        }

        if (relativeStartOffset < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relativeStartOffset),
                relativeStartOffset,
                "Continuity leg offset cannot be negative.");
        }

        var normalizedKinds = artifactKinds
            .Where(static kind => kind is AudioCaptureArtifactKind.Output or AudioCaptureArtifactKind.Microphone)
            .Distinct()
            .OrderBy(static kind => kind)
            .ToArray();
        if (normalizedKinds.Length == 0)
        {
            throw new ArgumentException("At least one raw continuity artifact kind is required.", nameof(artifactKinds));
        }

        Directory.CreateDirectory(legDirectory);
        var checkpointPath = Path.Combine(legDirectory, ContinuityLegCheckpointFileName);
        var partialPath = $"{checkpointPath}.partial";
        var checkpoint = new RecordingContinuityLegCheckpoint(
            CurrentSchemaVersion,
            sessionId,
            legIndex,
            relativeStartOffset,
            normalizedKinds);
        try
        {
            await using (var stream = new FileStream(
                             partialPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    checkpoint,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(partialPath, checkpointPath, overwrite: true);
            return checkpointPath;
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public static IReadOnlyList<AudioCaptureArtifactSnapshot> DiscoverContinuityLegArtifacts(
        string tempSessionDirectory,
        Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(tempSessionDirectory) || sessionId == Guid.Empty)
        {
            return [];
        }

        string[] legDirectories;
        try
        {
            var legsRoot = Path.Combine(tempSessionDirectory, "legs");
            if (!Directory.Exists(legsRoot))
            {
                return [];
            }

            legDirectories = Directory.GetDirectories(legsRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return [];
        }

        var artifacts = new List<AudioCaptureArtifactSnapshot>();
        foreach (var legDirectory in legDirectories.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var checkpoint = ReadContinuityLegCheckpoint(legDirectory);
            if (checkpoint is null
                || checkpoint.SchemaVersion != CurrentSchemaVersion
                || checkpoint.SessionId != sessionId
                || checkpoint.LegIndex <= 0
                || checkpoint.RelativeStartOffset < TimeSpan.Zero
                || !int.TryParse(
                    Path.GetFileName(legDirectory),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var directoryLegIndex)
                || directoryLegIndex != checkpoint.LegIndex)
            {
                continue;
            }

            try
            {
                var candidates = checkpoint.ArtifactKinds
                    .Where(static kind => kind is AudioCaptureArtifactKind.Output or AudioCaptureArtifactKind.Microphone)
                    .Distinct()
                    .Select(kind => new
                    {
                        Kind = kind,
                        Path = Path.Combine(
                            legDirectory,
                            kind == AudioCaptureArtifactKind.Microphone ? "mic.wav" : "output.wav")
                    })
                    .Where(static candidate => File.Exists(candidate.Path))
                    .Select(static candidate => new
                    {
                        candidate.Kind,
                        candidate.Path,
                        Info = new FileInfo(candidate.Path)
                    })
                    .Where(static candidate => candidate.Info.Length > 0)
                    .ToArray();
                if (candidates.Length == 0)
                {
                    continue;
                }

                var earliestCreationUtc = candidates.Min(static candidate => candidate.Info.CreationTimeUtc);
                foreach (var candidate in candidates)
                {
                    var sourceOffset = candidate.Info.CreationTimeUtc - earliestCreationUtc;
                    if (sourceOffset < TimeSpan.Zero)
                    {
                        sourceOffset = TimeSpan.Zero;
                    }

                    artifacts.Add(new AudioCaptureArtifactSnapshot(
                        candidate.Kind,
                        candidate.Path,
                        candidate.Info.Length,
                        new DateTimeOffset(candidate.Info.LastWriteTimeUtc),
                        checkpoint.RelativeStartOffset + sourceOffset));
                }
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException
                                              or OverflowException)
            {
                // A damaged leg remains isolated; other checkpointed legs can still recover.
            }
        }

        return artifacts;
    }

    private static RecordingContinuityLegCheckpoint? ReadContinuityLegCheckpoint(string legDirectory)
    {
        var checkpointPath = Path.Combine(legDirectory, ContinuityLegCheckpointFileName);
        if (!File.Exists(checkpointPath))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(checkpointPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<RecordingContinuityLegCheckpoint>(stream, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Startup recovery can reconcile an abandoned partial manifest.
        }
    }
}

internal sealed record RecordingSourceManifest(
    int SchemaVersion,
    Guid SessionId,
    DateTimeOffset WrittenAtUtc,
    IReadOnlyList<RecordingSourceManifestEntry> Artifacts);

internal sealed record RecordingSourceManifestEntry(
    AudioCaptureArtifactKind Kind,
    string Path,
    long BytesWritten,
    DateTimeOffset FinalizedAtUtc,
    TimeSpan RelativeStartOffset);

internal sealed record RecordingContinuityLegCheckpoint(
    int SchemaVersion,
    Guid SessionId,
    int LegIndex,
    TimeSpan RelativeStartOffset,
    IReadOnlyList<AudioCaptureArtifactKind> ArtifactKinds);

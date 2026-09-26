using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.compatibility
/// </remarks>
public sealed class MacOSRecordingArtifactDeletionTests
{
    [Fact]
    public void DeletesCurrentAndLegacyArtifactsWithinTheCheckpointedSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-delete-{Guid.NewGuid():N}");
        var paths = new LocalAppPaths("isTranscribe", root);
        var settings = ApplicationSettings.Default with
        {
            ReleaseV2 = ReleaseV2Settings.Default with
            {
                RecordingsFolder = Path.Combine(root, "recordings")
            }
        };
        var sessionId = Guid.NewGuid();
        var directory = Path.Combine(settings.ReleaseV2.RecordingsFolder, sessionId.ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var mp3 = Path.Combine(directory, "current.mp3");
            var m4a = Path.Combine(directory, "legacy.m4a");
            File.WriteAllBytes(mp3, [1]);
            File.WriteAllBytes(m4a, [2]);
            var service = new MacOSRecordingArtifactDeletionService(
                paths,
                new ArtifactPathResolver(paths),
                () => settings);

            service.DeleteRecentArtifacts(new RecentRecordingRemovalWorkItem(
                sessionId.ToString("N"),
                DateTimeOffset.UtcNow,
                "ready",
                null,
                null,
                m4a,
                mp3,
                null,
                null,
                null));

            Assert.False(File.Exists(mp3));
            Assert.False(File.Exists(m4a));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsArtifactOutsideOwnedRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-delete-{Guid.NewGuid():N}");
        var paths = new LocalAppPaths("isTranscribe", root);
        var service = new MacOSRecordingArtifactDeletionService(
            paths,
            new ArtifactPathResolver(paths),
            () => ApplicationSettings.Default);
        var outside = Path.Combine(Path.GetTempPath(), "unowned.mp3");

        Assert.Throws<InvalidOperationException>(() => service.DeleteRecentArtifacts(
            new RecentRecordingRemovalWorkItem(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                "ready",
                null,
                null,
                null,
                outside,
                null,
                null,
                null)));
    }
}

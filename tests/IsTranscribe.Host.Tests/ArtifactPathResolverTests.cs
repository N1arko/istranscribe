using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class ArtifactPathResolverTests
{
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    [Fact]
    public void CanonicalTranscriptPathsAreStableInsideTheSessionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-artifacts-{Guid.NewGuid():N}");
        var paths = new LocalAppPaths("isTranscribe", root);
        var resolver = new ArtifactPathResolver(paths);
        var settings = ApplicationSettings.Default;
        var sessionId = Guid.Parse("7af3431a-5d65-4a61-b905-4248b5466fa2");
        var createdAt = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        var markdown = resolver.GetCanonicalTranscriptFilePath(settings, sessionId, createdAt, "md");
        var json = resolver.GetCanonicalTranscriptFilePath(settings, sessionId, createdAt, ".JSON");
        var staged = resolver.GetStagedTranscriptFilePath(
            settings,
            sessionId,
            createdAt,
            "job-1",
            "md");

        Assert.Equal(Path.GetDirectoryName(markdown), Path.GetDirectoryName(json));
        Assert.EndsWith($"{sessionId:N}.transcript.md", markdown, StringComparison.Ordinal);
        Assert.EndsWith($"{sessionId:N}.transcript.json", json, StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(markdown), Path.GetDirectoryName(staged));
        Assert.Contains("job-1.partial", staged, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSessionDirectoryPath_UsesYearMonthAndSessionGuid()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var resolver = new ArtifactPathResolver(paths);
            var sessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var createdAtUtc = new DateTimeOffset(2026, 4, 5, 12, 30, 0, TimeSpan.Zero);

            var sessionDirectory = resolver.GetSessionDirectoryPath(ApplicationSettings.Default, sessionId, createdAtUtc);

            Assert.Equal(
                Path.Combine(paths.DefaultRecordingsDirectory, "2026", "04", "aaaaaaaabbbbccccddddeeeeeeeeeeee"),
                sessionDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetTranscriptFilePath_PlacesTranscriptInsideSessionDirectory_WhenFoldersMatch()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var recordingsRoot = Path.Combine(root, "Recordings");
            var settings = ApplicationSettings.Default with
            {
                Storage = ApplicationSettings.Default.Storage with
                {
                    RecordingsFolder = recordingsRoot,
                    TranscriptsFolder = recordingsRoot
                }
            };

            var paths = new LocalAppPaths("isTranscribe", root);
            var resolver = new ArtifactPathResolver(paths);
            var sessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var createdAtUtc = new DateTimeOffset(2026, 4, 5, 12, 30, 0, TimeSpan.Zero);

            var transcriptPath = resolver.GetTranscriptFilePath(settings, sessionId, createdAtUtc, "Zoom", "ask", "md");

            Assert.Contains(Path.Combine("2026", "04", "aaaaaaaabbbbccccddddeeeeeeeeeeee"), transcriptPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".md", transcriptPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#storage
    /// </summary>
    [Fact]
    public void PrimaryAndStagedAudioPaths_ShareTheConfiguredReleaseV2SessionDirectory()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var recordingsRoot = Path.Combine(root, "Release recordings");
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = IsTranscribe.Core.Settings.ReleaseV2Settings.Default with
                {
                    RecordingsFolder = recordingsRoot
                }
            };
            var resolver = new ArtifactPathResolver(new LocalAppPaths("isTranscribe", root));
            var sessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var createdAtUtc = new DateTimeOffset(2026, 7, 12, 15, 45, 0, TimeSpan.Zero);

            var primaryPath = resolver.GetPrimaryAudioFilePath(
                settings,
                sessionId,
                createdAtUtc,
                "Zen / Google Meet");
            var stagedPath = resolver.GetStagedPrimaryAudioFilePath(
                settings,
                sessionId,
                createdAtUtc,
                "Zen / Google Meet");

            Assert.Equal(
                Path.GetDirectoryName(primaryPath),
                Path.GetDirectoryName(stagedPath),
                ignoreCase: true);
            Assert.StartsWith(recordingsRoot, primaryPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".mp3", primaryPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".mp3.partial", stagedPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Zen_Google Meet", primaryPath, StringComparison.Ordinal);
            Assert.Contains("aaaaaaaa", primaryPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

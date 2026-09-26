using System.Text.Json;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// </summary>
public sealed class TranscriptArtifactMaterializerTests
{
    [Fact]
    public async Task StageValidatesBothArtifactsAndPromotionReplacesOldPair()
    {
        var root = CreateRoot();
        try
        {
            var materializer = new TranscriptArtifactMaterializer(
                new ArtifactPathResolver(new LocalAppPaths("isTranscribe", root)));
            var settings = SettingsFor(root);
            var document = CreateDocument();

            var staged = await materializer.StageAsync(settings, document, CancellationToken.None);

            Assert.True(File.Exists(staged.StagedMarkdownPath));
            Assert.True(File.Exists(staged.StagedJsonPath));
            Assert.False(File.Exists(staged.FinalMarkdownPath));
            Assert.False(File.Exists(staged.FinalJsonPath));
            await File.WriteAllTextAsync(staged.FinalMarkdownPath, "old markdown");
            await File.WriteAllTextAsync(staged.FinalJsonPath, "{\"old\":true}");

            await materializer.PublishAsync(
                staged,
                static (_, _) => ValueTask.CompletedTask,
                CancellationToken.None);
            await materializer.PublishAsync(
                staged,
                static (_, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            var markdown = await File.ReadAllTextAsync(staged.FinalMarkdownPath);
            var json = await File.ReadAllTextAsync(staged.FinalJsonPath);
            Assert.Contains("# Weekly sync", markdown, StringComparison.Ordinal);
            Assert.Contains("## Transcript", markdown, StringComparison.Ordinal);
            Assert.Contains("Hello world", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("old markdown", markdown, StringComparison.Ordinal);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("remote.groq", parsed.RootElement.GetProperty("engine_id").GetString());
            Assert.Equal("Hello world", parsed.RootElement.GetProperty("text").GetString());
            Assert.False(File.Exists(staged.StagedMarkdownPath));
            Assert.False(File.Exists(staged.StagedJsonPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptedStagedPairCannotReplaceExistingArtifacts()
    {
        var root = CreateRoot();
        try
        {
            var materializer = new TranscriptArtifactMaterializer(
                new ArtifactPathResolver(new LocalAppPaths("isTranscribe", root)));
            var staged = await materializer.StageAsync(
                SettingsFor(root),
                CreateDocument(),
                CancellationToken.None);
            await File.WriteAllTextAsync(staged.FinalMarkdownPath, "old markdown");
            await File.WriteAllTextAsync(staged.FinalJsonPath, "{\"old\":true}");
            await File.AppendAllTextAsync(staged.StagedJsonPath, "corruption");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                materializer.PublishAsync(
                        staged,
                        static (_, _) => ValueTask.CompletedTask,
                        CancellationToken.None)
                    .AsTask());

            Assert.Equal("old markdown", await File.ReadAllTextAsync(staged.FinalMarkdownPath));
            Assert.Equal("{\"old\":true}", await File.ReadAllTextAsync(staged.FinalJsonPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DatabasePublishFailureAfterPromotionRestoresPreviousPair()
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var settings = SettingsFor(root);
            var staged = await materializer.StageAsync(
                settings,
                CreateDocument(),
                CancellationToken.None);
            await WritePreviousPairAsync(staged);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                materializer.PublishAsync(
                        staged,
                        (published, _) =>
                        {
                            AssertPublishedPair(published);
                            return ValueTask.FromException(
                                new InvalidOperationException("Synthetic database failure."));
                        },
                        CancellationToken.None)
                    .AsTask());

            Assert.Equal("Synthetic database failure.", exception.Message);
            await AssertPreviousPairAsync(staged);
            Assert.Empty(materializer.FindPendingPublicationJournals(settings));
            AssertNoPublicationScratch(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationAfterFilesystemPromotionRestoresPreviousPair()
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var settings = SettingsFor(root);
            var staged = await materializer.StageAsync(
                settings,
                CreateDocument(),
                CancellationToken.None);
            await WritePreviousPairAsync(staged);
            using var cancellation = new CancellationTokenSource();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                materializer.PublishAsync(
                        staged,
                        (published, cancellationToken) =>
                        {
                            AssertPublishedPair(published);
                            cancellation.Cancel();
                            return ValueTask.FromCanceled(cancellationToken);
                        },
                        cancellation.Token)
                    .AsTask());

            await AssertPreviousPairAsync(staged);
            Assert.Empty(materializer.FindPendingPublicationJournals(settings));
            AssertNoPublicationScratch(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JournalInspectionIdentifiesPromotedDatabaseCommitCrashWindow()
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var settings = SettingsFor(root);
            var staged = await materializer.StageAsync(
                settings,
                CreateDocument(),
                CancellationToken.None);
            await WritePreviousPairAsync(staged);
            var databaseJobCompleted = false;

            await materializer.PublishAsync(
                staged,
                async (published, cancellationToken) =>
                {
                    databaseJobCompleted = true;
                    var journalPath = Assert.Single(materializer.FindPendingPublicationJournals(settings));
                    var pending = await materializer.GetPendingPublicationAsync(
                        journalPath,
                        cancellationToken);

                    Assert.True(databaseJobCompleted);
                    Assert.Equal(staged.JobId, pending.JobId);
                    Assert.Equal(TranscriptArtifactPublicationPhase.Promoted, pending.Phase);
                    Assert.Equal(published.FinalMarkdownPath, pending.FinalMarkdownPath);
                    Assert.Equal(published.FinalJsonPath, pending.FinalJsonPath);
                    Assert.Equal(published.MarkdownSha256, pending.MarkdownSha256);
                    Assert.Equal(published.JsonSha256, pending.JsonSha256);
                },
                CancellationToken.None);

            Assert.True(databaseJobCompleted);
            Assert.Empty(materializer.FindPendingPublicationJournals(settings));
            AssertNoPublicationScratch(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(TranscriptArtifactRecoveryAction.RollBack, true)]
    [InlineData(TranscriptArtifactRecoveryAction.Complete, false)]
    public async Task DurableJournalSupportsRestartRollbackOrCompletion(
        TranscriptArtifactRecoveryAction action,
        bool expectPreviousPair)
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var settings = SettingsFor(root);
            var staged = await materializer.StageAsync(
                settings,
                CreateDocument(),
                CancellationToken.None);
            await WritePreviousPairAsync(staged);

            var pending = await materializer.BeginPublicationAsync(staged, CancellationToken.None);

            Assert.True(File.Exists(pending.JournalPath));
            Assert.Equal([pending.JournalPath], materializer.FindPendingPublicationJournals(settings));
            AssertPublishedPair(pending.Artifacts);
            var restartedMaterializer = CreateMaterializer(root);

            await restartedMaterializer.RecoverPublicationAsync(
                pending.JournalPath,
                action,
                CancellationToken.None);

            if (expectPreviousPair)
            {
                await AssertPreviousPairAsync(staged);
            }
            else
            {
                AssertPublishedPair(pending.Artifacts);
            }

            Assert.Empty(restartedMaterializer.FindPendingPublicationJournals(settings));
            AssertNoPublicationScratch(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedJournalIsRejectedAndCanBePreservedInExplicitQuarantine()
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var settings = SettingsFor(root);
            var journalDirectory = Path.Combine(
                Assert.IsType<string>(settings.Storage.RecordingsFolder),
                "journal-fixture");
            Directory.CreateDirectory(journalDirectory);
            var journalPath = Path.Combine(
                journalDirectory,
                "fixture.transcript.json.publication-journal");
            await File.WriteAllBytesAsync(journalPath, new byte[(256 * 1024) + 1]);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                materializer.GetPendingPublicationAsync(journalPath, CancellationToken.None).AsTask());
            Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);

            var quarantined = await materializer.QuarantinePublicationJournalAsync(
                journalPath,
                "journal-invalid",
                CancellationToken.None);

            Assert.NotNull(quarantined);
            Assert.Equal(Path.GetFullPath(journalPath), quarantined.OriginalPath);
            Assert.Equal("journal-invalid", quarantined.ReasonCode);
            Assert.False(File.Exists(journalPath));
            Assert.True(File.Exists(quarantined.QuarantinePath));
            Assert.Equal((256 * 1024) + 1, new FileInfo(quarantined.QuarantinePath).Length);
            Assert.Empty(materializer.FindPendingPublicationJournals(settings));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledStageDiscardDeletesOnlyExpectedPartialPayloads()
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var staged = await materializer.StageAsync(
                SettingsFor(root),
                CreateDocument(),
                CancellationToken.None);

            Assert.True(materializer.DiscardStagedArtifacts(staged));

            Assert.False(File.Exists(staged.StagedMarkdownPath));
            Assert.False(File.Exists(staged.StagedJsonPath));
            Assert.False(File.Exists(staged.FinalMarkdownPath));
            Assert.False(File.Exists(staged.FinalJsonPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledStageDiscardRejectsAStoredPathOutsideTheCanonicalJobPattern()
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var staged = await materializer.StageAsync(
                SettingsFor(root),
                CreateDocument(),
                CancellationToken.None);
            var unrelatedPath = Path.Combine(
                Path.GetDirectoryName(staged.StagedMarkdownPath)!,
                ".unrelated.partial");
            File.Copy(staged.StagedMarkdownPath, unrelatedPath);

            Assert.Throws<InvalidDataException>(() => materializer.DiscardStagedArtifacts(
                staged with { StagedMarkdownPath = unrelatedPath }));

            Assert.True(File.Exists(unrelatedPath));
            Assert.True(File.Exists(staged.StagedMarkdownPath));
            Assert.True(File.Exists(staged.StagedJsonPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledStageDiscardChecksBothHashesBeforeDeletingEitherPartial()
    {
        var root = CreateRoot();
        try
        {
            var materializer = CreateMaterializer(root);
            var staged = await materializer.StageAsync(
                SettingsFor(root),
                CreateDocument(),
                CancellationToken.None);
            await File.WriteAllTextAsync(staged.StagedJsonPath, "tampered");

            Assert.False(materializer.DiscardStagedArtifacts(staged));

            Assert.True(File.Exists(staged.StagedMarkdownPath));
            Assert.True(File.Exists(staged.StagedJsonPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NormalizedTranscriptDocument CreateDocument()
    {
        var source = new AudioSourceFingerprint(
            new string('a', 64),
            1024,
            TimeSpan.FromSeconds(2),
            "mp3");
        var merged = new MergedTranscript(
            "Hello world",
            "en",
            "whisper-large-v3-turbo",
            [new TranscriptionSegment("Hello world", TimeSpan.Zero, TimeSpan.FromSeconds(2))],
            [
                new MergedTranscriptChunk(
                    "chunk-1",
                    0,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    "Hello world",
                    "request-1",
                    new TranscriptionUsage(AudioSeconds: 2))
            ],
            new TranscriptionUsage(AudioSeconds: 2));
        return NormalizedTranscriptDocument.Create(
            new TranscriptDocumentContext(
                "job-1",
                Guid.Parse("7af3431a-5d65-4a61-b905-4248b5466fa2"),
                "Weekly sync",
                new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.FromHours(-3)),
                TimeSpan.FromSeconds(2),
                "remote.groq",
                "whisper-large-v3-turbo",
                "auto",
                source),
            merged);
    }

    private static ApplicationSettings SettingsFor(string root) =>
        ApplicationSettings.Default with
        {
            Storage = ApplicationSettings.Default.Storage with
            {
                RecordingsFolder = Path.Combine(root, "recordings")
            }
        };

    private static TranscriptArtifactMaterializer CreateMaterializer(string root) =>
        new(new ArtifactPathResolver(new LocalAppPaths("isTranscribe", root)));

    private static async Task WritePreviousPairAsync(StagedTranscriptArtifacts staged)
    {
        await File.WriteAllTextAsync(staged.FinalMarkdownPath, "old markdown");
        await File.WriteAllTextAsync(staged.FinalJsonPath, "{\"old\":true}");
    }

    private static async Task AssertPreviousPairAsync(StagedTranscriptArtifacts staged)
    {
        Assert.Equal("old markdown", await File.ReadAllTextAsync(staged.FinalMarkdownPath));
        Assert.Equal("{\"old\":true}", await File.ReadAllTextAsync(staged.FinalJsonPath));
    }

    private static void AssertPublishedPair(PublishedTranscriptArtifacts published)
    {
        Assert.Contains(
            "Hello world",
            File.ReadAllText(published.FinalMarkdownPath),
            StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllText(published.FinalJsonPath));
        Assert.Equal("Hello world", json.RootElement.GetProperty("text").GetString());
    }

    private static void AssertNoPublicationScratch(string root)
    {
        Assert.Empty(Directory.EnumerateFiles(root, "*.publication-journal", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(root, "*.publication-backup", SearchOption.AllDirectories));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-transcript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}

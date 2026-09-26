using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;
using IsTranscribe.Host.Transcription;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class TranscriptionBackgroundWorkerTests
{
    [Fact]
    public async Task ProcessOnceAsync_CompletesQueuedSessionAndWritesArtifacts()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var logger = new BootstrapFileLogger(Path.Combine(root, "logs", "host.log"));
            var artifactResolver = new ArtifactPathResolver(paths);

            var createdAt = new DateTimeOffset(2026, 4, 13, 10, 0, 0, TimeSpan.Zero);
            var sessionId = Guid.NewGuid();
            var audioPath = Path.Combine(root, "recordings", "session.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
            await File.WriteAllBytesAsync(audioPath, [1, 2, 3, 4], CancellationToken.None);

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(sessionId, createdAt, "manual", "mixed", "out-1", "mic-1") with
                {
                    Status = "saved",
                    SourceApp = "Zoom",
                    TranscriptionStatus = "queued",
                    QueuedAtUtc = createdAt,
                    AudioOutputPath = audioPath,
                    TranscriptionModel = "whisper-v3-turbo",
                    DiarizationEnabled = true,
                    Language = "en"
                },
                CancellationToken.None);

            var settings = ApplicationSettings.Default with
            {
                OnboardingCompleted = true,
                Storage = ApplicationSettings.Default.Storage with
                {
                    RecordingsFolder = Path.Combine(root, "recordings"),
                    TranscriptsFolder = Path.Combine(root, "transcripts")
                }
            };

            var worker = new TranscriptionBackgroundWorker(
                logger,
                repository,
                artifactResolver,
                new FakeTranscriptionProvider(),
                () => settings,
                () => new AppSecrets("test-key"));

            await worker.ProcessOnceAsync(CancellationToken.None);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT transcription_status, transcript_md_path, transcript_json_path FROM meeting_session WHERE id = $id;";
            command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("completed", reader.GetString(0));
            var markdownPath = reader.GetString(1);
            var jsonPath = reader.GetString(2);
            Assert.True(File.Exists(markdownPath));
            Assert.True(File.Exists(jsonPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public ValueTask<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, string apiKey, CancellationToken cancellationToken)
            => ValueTask.FromResult(new TranscriptionResponse("hello from provider", "{\"text\":\"hello from provider\"}", false));
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Models;
using Xunit;
using Xunit.Abstractions;

namespace IsTranscribe.Application.Tests;

public sealed partial class LocalWhisperApplicationIntegrationTests(ITestOutputHelper output)
{
    // Explicit one-provider opt-in. Secrets stay in memory and never enter artifacts or logs.
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#verification
    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#verification
    [Theory]
    [InlineData("groq")]
    [InlineData("openrouter")]
    public async Task RealOnlineProviderProducesSpeakerTranscriptThroughDurableQueue(string provider)
    {
        if (Environment.GetEnvironmentVariable("ISTRANSCRIBE_ONLINE_ACCEPTANCE_PROVIDER") != provider) return;
        var assets = RequiredEnvironment("ISTRANSCRIBE_SPEAKER_ACCEPTANCE_ASSETS");
        var worker = RequiredEnvironment("ISTRANSCRIBE_WHISPER_ACCEPTANCE_WORKER");
        var twoVoices = RequiredEnvironment("ISTRANSCRIBE_SPEAKER_ACCEPTANCE_TWO_VOICES");
        var microphone = Environment.GetEnvironmentVariable("ISTRANSCRIBE_ONLINE_ACCEPTANCE_MIC_WAV")
            ?? RequiredEnvironment("ISTRANSCRIBE_WHISPER_ACCEPTANCE_PT_WAV");
        var microphoneLanguage = Environment.GetEnvironmentVariable("ISTRANSCRIBE_ONLINE_ACCEPTANCE_MIC_LANGUAGE") ?? "pt";
        var secrets = RequiredEnvironment("ISTRANSCRIBE_ONLINE_ACCEPTANCE_SECRETS_DIRECTORY");
        var file = Path.Combine(secrets, provider == "groq" ? "groq api-key.md" : "openrouter.md");
        var candidates = Regex.Matches(await File.ReadAllTextAsync(file), provider == "groq" ? @"gsk_[A-Za-z0-9]{20,}" : @"sk-or-v1-[A-Za-z0-9]{20,}")
            .Select(static match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(candidates.Length == 1, "Expected exactly one unique credential in the explicitly scoped provider file.");
        var vault = new AcceptanceSecretVault(candidates[0]);
        using var observer = new AcceptanceHttpObserver(output);
        using var http = new HttpClient(observer) { Timeout = TimeSpan.FromSeconds(90) };
        ITranscriptionEngine engine = provider == "groq" ? new GroqTranscriptionEngine(http, vault) : new OpenRouterTranscriptionEngine(http, vault);
        var model = provider == "groq" ? GroqTranscriptionEngine.DefaultModelId : OpenRouterTranscriptionEngine.DefaultModelId;
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(durationSeconds: 16, prepare: false,
            realOutputWave: twoVoices, realMicrophoneWave: microphone, remoteEngineId: engine.Capabilities.EngineId, remoteModelId: model);
        var processor = new SpeakerAwareChunkProcessor(new SpeakerDiarizationClient(worker, new DiarizationAssets(assets)), new PassThroughChunkMaterializer());
        await using var queue = fixture.CreateQueueWorker(engine, new AudioChunkPlanner(), speakerProcessor: processor);
        await queue.StartAsync(CancellationToken.None);
        var completed = await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed, timeoutSeconds: 180, failOnAttention: true);
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(completed.TranscriptJsonPath!));
        var document = artifact.RootElement;
        Assert.Equal("speaker-transcript/v1", document.GetProperty("schema").GetString());
        var ids = document.GetProperty("speakers").EnumerateArray().Select(static speaker => speaker.GetProperty("id").GetString()).ToArray();
        Assert.Contains("self", ids); Assert.Contains("remote:1", ids); Assert.Contains("remote:2", ids);
        Assert.DoesNotContain(ids, static id => id!.StartsWith("unknown:", StringComparison.Ordinal));
        var sources = document.GetProperty("source_recognitions").EnumerateArray().ToArray();
        Assert.Equal(2, sources.Length);
        Assert.All(sources, source => Assert.Equal(model, source.GetProperty("resolved_model_id").GetString()));
        var languages = sources.Select(static source => source.GetProperty("detected_language").GetString()).ToArray();
        Assert.Contains(microphoneLanguage, languages); Assert.Contains("en", languages);
        Assert.True(document.GetProperty("turns").GetArrayLength() >= 3);
        Assert.Equal(2, observer.UploadCount);
        Assert.True(File.Exists(job.Request.PrimaryAudioArtifactPath));
        Assert.True(File.Exists(completed.TranscriptMarkdownPath));
        Assert.DoesNotContain(candidates[0], await File.ReadAllTextAsync(completed.TranscriptJsonPath!));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            provider,
            model,
            speakers = ids,
            languages,
            turns = document.GetProperty("turns").GetArrayLength(),
            uploadCount = observer.UploadCount,
            sourceProvenance = sources,
            primaryPreserved = true
        }));
        var reader = new TranscriptionSessionContextReader(fixture.Paths);
        for (var i = 0; i < 100 && (await reader.GetAsync(completed.SessionId, CancellationToken.None))?.TempSessionPath is not null; i++)
            await Task.Delay(20);
        Assert.Null((await reader.GetAsync(completed.SessionId, CancellationToken.None))?.TempSessionPath);
    }

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Missing acceptance setting: " + name);

    private sealed class AcceptanceSecretVault(string credential) : ISecretVault
    {
        public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(credential);
        public ValueTask WriteAsync(string key, string? value, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Observe protocol shape only; do not print request/response bodies, transcript or credentials.
    private sealed class AcceptanceHttpObserver(ITestOutputHelper output) : DelegatingHandler(new HttpClientHandler())
    {
        public int UploadCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && ++UploadCount > 2) throw new InvalidOperationException("Acceptance upload budget exceeded.");
            var response = await base.SendAsync(request, cancellationToken);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                method = request.Method.Method,
                host = request.RequestUri!.Host,
                path = request.RequestUri.AbsolutePath,
                status = (int)response.StatusCode
            }));
            return response;
        }
    }
}

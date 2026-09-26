using System.Net;
using System.Net.Http.Headers;
using System.Text;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Transcription;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers.groq
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#verification
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </summary>
public sealed class GroqTranscriptionEngineTests
{
    [Theory]
    [InlineData("English", "en")]
    [InlineData("Portuguese", "pt")]
    [InlineData("Russian", "ru")]
    [InlineData("Japanese", "ja")]
    [InlineData("Arabic", "ar")]
    [InlineData("uk", "uk")]
    [InlineData("unknown language", null)]
    public async Task ProviderLanguageNamesNormalizeIndependentlyOfUiLanguage(string language, string? expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            text = "Hello",
            language,
            words = new[] { new { word = "Hello", start = 0, end = 1 } }
        });
        using var client = new HttpClient(new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, json)));
        var engine = new GroqTranscriptionEngine(client, new MemorySecretVault((RemoteTranscriptionSecrets.Groq, "synthetic-key")));
        var input = CreateAudioFixture();
        try
        {
            var result = await engine.TranscribeAsync(new(Guid.NewGuid(), input, GroqTranscriptionEngine.DefaultModelId), null, CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal(expected, result.DetectedLanguage);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task TextOnlySuccessCannotCompleteSpeakerAwareTranscription()
    {
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, """{"text":"Synthetic text","language":"en"}"""));
        using var client = new HttpClient(handler);
        var engine = new GroqTranscriptionEngine(client, new MemorySecretVault((RemoteTranscriptionSecrets.Groq, "synthetic-key")));
        var input = CreateAudioFixture();
        try
        {
            var result = await engine.TranscribeAsync(new(Guid.NewGuid(), input, "whisper-large-v3-turbo"), null, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal("timestamp_capability_missing", result.Error?.Code);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task CompletedRequestUsesMultipartProfileAndMapsNormalizedResult()
    {
        const string syntheticCredential = "synthetic-groq-credential";
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "text": "Hello world",
              "language": "en",
              "duration": 2.5,
              "words": [{"word":"Hello","start":0,"end":1},{"word":"world","start":1,"end":2.5}],
              "segments": [
                { "start": 0.0, "end": 2.5, "text": "Hello world" }
              ]
            }
            """,
            requestId: "groq-request-1"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://groq.test/openai/v1/") };
        var engine = new GroqTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.Groq, syntheticCredential)));
        var inputPath = CreateAudioFixture();

        try
        {
            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(
                    Guid.NewGuid(),
                    inputPath,
                    GroqTranscriptionEngine.AccuracyModelId,
                    "en"),
                progress: null,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("Hello world", result.Text);
            Assert.Equal("en", result.DetectedLanguage);
            Assert.Equal("groq-request-1", result.Metadata?.RequestId);
            Assert.Equal(2.5, result.Metadata?.Usage?.AudioSeconds);
            Assert.Equal(GroqTranscriptionEngine.AccuracyModelId, result.Metadata?.ResolvedModelId);
            var segment = Assert.Single(result.Segments);
            Assert.Equal(TimeSpan.Zero, segment.Start);
            Assert.Equal(TimeSpan.FromSeconds(2.5), segment.End);

            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal("https://groq.test/openai/v1/audio/transcriptions", handler.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", handler.Authorization?.Scheme);
            Assert.Equal(syntheticCredential, handler.Authorization?.Parameter);
            Assert.Contains("name=file", handler.Body, StringComparison.Ordinal);
            Assert.Contains("name=model", handler.Body, StringComparison.Ordinal);
            Assert.Contains(GroqTranscriptionEngine.AccuracyModelId, handler.Body, StringComparison.Ordinal);
            Assert.Contains("name=response_format", handler.Body, StringComparison.Ordinal);
            Assert.Contains("verbose_json", handler.Body, StringComparison.Ordinal);
            Assert.Contains("timestamp_granularities[]", handler.Body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task MissingCredentialReturnsAttentionWithoutNetworkCall()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must stay idle."));
        using var client = new HttpClient(handler);
        var engine = new GroqTranscriptionEngine(client, new MemorySecretVault());

        var result = await engine.TranscribeAsync(
            new TranscriptionRequest(Guid.NewGuid(), "missing.mp3"),
            progress: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_key", result.Error?.Code);
        Assert.Equal(TranscriptionFailureDisposition.AttentionRequired, result.Error?.Disposition);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DiscoveryReturnsOnlyAccountAvailableSpeechModels()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "data": [
                { "id": "whisper-large-v3" },
                { "id": "whisper-large-v3-turbo" },
                { "id": "unrelated-chat-model" }
              ]
            }
            """));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://groq.test/openai/v1/") };
        var engine = new GroqTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.Groq, "synthetic")));

        var models = await engine.DiscoverModelsAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal(GroqTranscriptionEngine.DefaultModelId, model.Id);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://groq.test/openai/v1/models", handler.RequestUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData(401, TranscriptionErrorCategory.Authentication, "invalid_key", TranscriptionFailureDisposition.AttentionRequired)]
    [InlineData(403, TranscriptionErrorCategory.Authentication, "invalid_key", TranscriptionFailureDisposition.AttentionRequired)]
    [InlineData(413, TranscriptionErrorCategory.PayloadTooLarge, "payload_too_large", TranscriptionFailureDisposition.SplitInput)]
    [InlineData(429, TranscriptionErrorCategory.RateLimited, "rate_limited", TranscriptionFailureDisposition.TryAgain)]
    [InlineData(498, TranscriptionErrorCategory.RateLimited, "rate_limited", TranscriptionFailureDisposition.TryAgain)]
    [InlineData(503, TranscriptionErrorCategory.EngineUnavailable, "engine_unavailable", TranscriptionFailureDisposition.TryAgain)]
    public async Task HttpFailuresMapToStableBoundedErrors(
        int status,
        TranscriptionErrorCategory category,
        string code,
        TranscriptionFailureDisposition disposition)
    {
        const string syntheticCredential = "credential-that-must-not-appear-in-errors";
        const string untrustedBody = "provider detail with private transcript content";
        var handler = new RecordingHandler(_ => JsonResponse((HttpStatusCode)status, untrustedBody));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://groq.test/openai/v1/") };
        var engine = new GroqTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.Groq, syntheticCredential)));
        var inputPath = CreateAudioFixture();

        try
        {
            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(Guid.NewGuid(), inputPath),
                progress: null,
                CancellationToken.None);

            Assert.Equal(category, result.Error?.Category);
            Assert.Equal(code, result.Error?.Code);
            Assert.Equal(disposition, result.Error?.Disposition);
            Assert.DoesNotContain(syntheticCredential, result.Error?.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(untrustedBody, result.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task RateLimitHonorsRetryAfterHeader()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = JsonResponse(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(75));
            return response;
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://groq.test/openai/v1/") };
        var engine = new GroqTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.Groq, "synthetic")));
        var inputPath = CreateAudioFixture();

        try
        {
            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(Guid.NewGuid(), inputPath),
                progress: null,
                CancellationToken.None);

            Assert.Equal(TimeSpan.FromSeconds(75), result.Error?.SuggestedDelay);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string body,
        string? requestId = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-request-id", requestId);
        }

        return response;
    }

    private static string CreateAudioFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"istranscribe-groq-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, [0x49, 0x44, 0x33, 0x04, 0x00]);
        return path;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }

    private sealed class MemorySecretVault(params (string Key, string Value)[] values) : ISecretVault
    {
        private readonly Dictionary<string, string> _values = values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_values.GetValueOrDefault(key));

        public ValueTask WriteAsync(string key, string? value, CancellationToken cancellationToken)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            return ValueTask.CompletedTask;
        }
    }
}

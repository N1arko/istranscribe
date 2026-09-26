using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Transcription;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// Synthetic OpenRouter HTTP contract coverage. Fixtures contain no live credentials or user data.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers.openrouter
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#verification
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public sealed class OpenRouterTranscriptionEngineTests
{
    private const string SyntheticCredential = "synthetic-openrouter-test-credential";
    private const string SyntheticProviderBody = "synthetic provider detail that must stay private";

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"model_id\":\"openai/whisper-large-v3-turbo\",\"tag\":\"groq\"}]}")]
    [InlineData("{}")]
    public async Task EveryPossibleEndpointMustHaveVerifiedZdrBeforeUpload(string policy)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must not upload")) { ZdrResponse = policy };
        using var client = new HttpClient(handler);
        var engine = new OpenRouterTranscriptionEngine(client, new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)));
        var input = CreateAudioFixture(".wav", [1, 2, 3]);
        try
        {
            var result = await engine.TranscribeAsync(new(Guid.NewGuid(), input, OpenRouterTranscriptionEngine.DefaultModelId), null, CancellationToken.None);
            Assert.Equal("zdr_route_unavailable", result.Error?.Code);
            Assert.Equal(1, handler.ZdrCheckCount);
            Assert.Equal(0, handler.CallCount);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task FrozenLegacyWhisperRouteRemainsAvailableWithExplicitNonZdrPolicy()
    {
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"text":"Hello","language":"English","words":[{"word":"Hello","start":0,"end":1}]}"""))
        { EndpointResponse = """{"data":{"endpoints":[{"tag":"openai"}]}}""" };
        using var client = new HttpClient(handler);
        var engine = new OpenRouterTranscriptionEngine(client, new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)));
        var input = CreateAudioFixture(".wav", [1, 2, 3]);
        try
        {
            var result = await engine.TranscribeAsync(new(Guid.NewGuid(), input, "openai/whisper-1", RequireZeroDataRetention: false), null, CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal("en", result.DetectedLanguage);
            Assert.Equal(0, handler.ZdrCheckCount);
            Assert.Equal(1, handler.CallCount);
        }
        finally { File.Delete(input); }
    }

    [Theory]
    [InlineData("{\"data\":{\"endpoints\":[]}}")]
    [InlineData("{\"data\":{\"endpoints\":[{\"tag\":\"openai\"},{\"tag\":\"unreviewed\"}]}}")]
    public async Task UnreviewedRouteIsRejectedBeforeAnyAudioUpload(string endpoints)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must not upload")) { EndpointResponse = endpoints };
        using var client = new HttpClient(handler);
        var engine = new OpenRouterTranscriptionEngine(client, new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)));
        var input = CreateAudioFixture(".wav", [1, 2, 3]);
        try
        {
            var result = await engine.TranscribeAsync(new(Guid.NewGuid(), input, OpenRouterTranscriptionEngine.DefaultModelId), null, CancellationToken.None);
            Assert.Equal("timestamp_capability_missing", result.Error?.Code);
            Assert.Equal(1, handler.RouteCheckCount);
            Assert.Equal(0, handler.CallCount);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task TextOnlySuccessCannotCompleteSpeakerAwareTranscription()
    {
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, """{"text":"Synthetic text","language":"en"}"""));
        using var client = new HttpClient(handler);
        var engine = new OpenRouterTranscriptionEngine(client, new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)));
        var input = CreateAudioFixture(".wav", [1, 2, 3]);
        try
        {
            var result = await engine.TranscribeAsync(new(Guid.NewGuid(), input, OpenRouterTranscriptionEngine.DefaultModelId), null, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal("timestamp_capability_missing", result.Error?.Code);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task CompletedRequestUsesRawBase64ZdrPolicyAndMapsNormalizedMetadata()
    {
        byte[] audioBytes = [0x49, 0x44, 0x33, 0x01, 0x02, 0x03];
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "text": "Synthetic transcript.",
              "words": [{"word":"Synthetic","start":0,"end":1},{"word":"transcript.","start":1,"end":2}],
              "language": "Portuguese",
              "model": "provider/resolved-whisper",
              "usage": {
                "seconds": 9.2,
                "input_tokens": 12,
                "output_tokens": 34,
                "cost": 0.0042
              }
            }
            """,
            requestId: "synthetic-openrouter-request-1",
            requestIdHeader: "x-generation-id"));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.test/api/v1/")
        };
        var engine = new OpenRouterTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)));
        var inputPath = CreateAudioFixture(".m4a", audioBytes);

        try
        {
            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(
                    Guid.NewGuid(),
                    inputPath,
                    ModelId: "openai/whisper-large-v3-turbo",
                    Language: "pt",
                    SourceStart: TimeSpan.FromSeconds(10),
                    SourceEnd: TimeSpan.FromSeconds(20)),
                progress: null,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("Synthetic transcript.", result.Text);
            Assert.Equal("pt", result.DetectedLanguage);
            Assert.Equal("provider/resolved-whisper", result.Metadata?.ResolvedModelId);
            Assert.Equal("synthetic-openrouter-request-1", result.Metadata?.RequestId);
            Assert.Equal(12, result.Metadata?.Usage?.InputUnits);
            Assert.Equal(34, result.Metadata?.Usage?.OutputUnits);
            Assert.Equal(9.2, result.Metadata?.Usage?.AudioSeconds);
            Assert.Equal(0.0042m, result.Metadata?.Usage?.ReportedCost);
            Assert.Equal("USD", result.Metadata?.Usage?.Currency);
            Assert.Equal(TimeSpan.FromSeconds(10), result.Metadata?.SourceStart);
            Assert.Equal(TimeSpan.FromSeconds(20), result.Metadata?.SourceEnd);

            Assert.Equal(1, handler.CallCount);
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal(
                "https://openrouter.test/api/v1/audio/transcriptions",
                handler.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", handler.Authorization?.Scheme);
            Assert.Equal(SyntheticCredential, handler.Authorization?.Parameter);
            Assert.Equal("application/json", handler.ContentType);

            using var body = JsonDocument.Parse(handler.Body);
            var request = body.RootElement;
            var inputAudio = request.GetProperty("input_audio");
            var encodedAudio = inputAudio.GetProperty("data").GetString();
            Assert.Equal("openai/whisper-large-v3-turbo", request.GetProperty("model").GetString());
            Assert.Equal("pt", request.GetProperty("language").GetString());
            Assert.Equal(0, request.GetProperty("temperature").GetDouble());
            Assert.Equal("m4a", inputAudio.GetProperty("format").GetString());
            Assert.Equal(Convert.ToBase64String(audioBytes), encodedAudio);
            Assert.False(encodedAudio!.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
            Assert.True(request.GetProperty("provider").GetProperty("zdr").GetBoolean());
            Assert.Equal("verbose_json", request.GetProperty("response_format").GetString());
            Assert.Contains("word", request.GetProperty("timestamp_granularities").EnumerateArray().Select(static item => item.GetString()));
            Assert.False(request.GetProperty("provider").TryGetProperty("only", out _));
            Assert.Equal(1, handler.RouteCheckCount);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ModelDiscoveryKeepsTranscriptionModelsAndRecommendsWhisper()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "data": [
                {
                  "id": "provider/general-text",
                  "name": "General Text",
                  "architecture": {
                    "input_modalities": ["text"],
                    "output_modalities": ["text"]
                  }
                },
                {
                  "id": "provider/audio-stt",
                  "name": "Audio STT",
                  "architecture": {
                    "input_modalities": ["audio"],
                    "output_modalities": ["text"]
                  }
                },
                {
                  "id": "openai/whisper-large-v3-turbo",
                  "name": "Synthetic Whisper",
                  "output_modalities": ["transcription"]
                },
                {
                  "id": "provider/direct-stt",
                  "name": "Direct STT",
                  "capabilities": {
                    "output_modalities": ["transcription"]
                  }
                },
                {
                  "id": "",
                  "name": "Invalid",
                  "output_modalities": ["transcription"]
                }
              ]
            }
            """));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.test/api/v1/")
        };
        var engine = new OpenRouterTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)));

        var models = await engine.DiscoverModelsAsync(CancellationToken.None);

        Assert.Equal(
            ["openai/whisper-large-v3-turbo"],
            models.Select(static model => model.Id));
        Assert.Equal("Synthetic Whisper", models[0].DisplayName);
        Assert.True(models[0].IsRecommended);
        Assert.All(models.Skip(1), static model => Assert.False(model.IsRecommended));
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            "https://openrouter.test/api/v1/models?output_modalities=transcription",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(SyntheticCredential, handler.Authorization?.Parameter);
        Assert.Equal(string.Empty, handler.Body);
    }

    [Fact]
    public async Task MissingModelOrCredentialKeepsTranscriptionAndDiscoveryOffline()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("Synthetic network handler must stay idle."));
        using var client = new HttpClient(handler);
        var keyedVault = new MemorySecretVault(
            (RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential));
        var keyedEngine = new OpenRouterTranscriptionEngine(client, keyedVault);

        var missingModel = await keyedEngine.TranscribeAsync(
            new TranscriptionRequest(Guid.NewGuid(), "synthetic-missing.mp3"),
            progress: null,
            CancellationToken.None);

        Assert.Equal("missing_model", missingModel.Error?.Code);
        Assert.Equal(TranscriptionFailureDisposition.AttentionRequired, missingModel.Error?.Disposition);
        Assert.Equal(0, keyedVault.ReadCount);

        var emptyVault = new MemorySecretVault();
        var unkeyedEngine = new OpenRouterTranscriptionEngine(client, emptyVault);
        var missingKey = await unkeyedEngine.TranscribeAsync(
            new TranscriptionRequest(
                Guid.NewGuid(),
                "synthetic-missing.mp3",
                ModelId: "openai/whisper-large-v3-turbo"),
            progress: null,
            CancellationToken.None);

        Assert.Equal("missing_key", missingKey.Error?.Code);
        Assert.Equal(TranscriptionFailureDisposition.AttentionRequired, missingKey.Error?.Disposition);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unkeyedEngine.DiscoverModelsAsync(CancellationToken.None).AsTask());
        Assert.Equal(2, emptyVault.ReadCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(402, TranscriptionErrorCategory.PaymentRequired, "insufficient_credit", TranscriptionFailureDisposition.AttentionRequired)]
    [InlineData(413, TranscriptionErrorCategory.PayloadTooLarge, "payload_too_large", TranscriptionFailureDisposition.SplitInput)]
    [InlineData(429, TranscriptionErrorCategory.RateLimited, "rate_limited", TranscriptionFailureDisposition.TryAgain)]
    [InlineData(503, TranscriptionErrorCategory.EngineUnavailable, "engine_unavailable", TranscriptionFailureDisposition.TryAgain)]
    public async Task HttpFailuresMapToStableRedactedErrors(
        int status,
        TranscriptionErrorCategory category,
        string code,
        TranscriptionFailureDisposition disposition)
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = JsonResponse(
                (HttpStatusCode)status,
                $$"""
                { "error": { "message": "{{SyntheticProviderBody}}" } }
                """,
                requestId: "synthetic-openrouter-error-request");
            if (status == 429)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(61));
            }

            return response;
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.test/api/v1/")
        };
        var engine = new OpenRouterTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)),
            new OpenRouterTranscriptionOptions(RequireZeroDataRetention: false));
        var inputPath = CreateAudioFixture(".mp3", [0x49, 0x44, 0x33, 0x04]);

        try
        {
            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(
                    Guid.NewGuid(),
                    inputPath,
                    ModelId: "openai/whisper-large-v3-turbo"),
                progress: null,
                CancellationToken.None);

            Assert.Equal(category, result.Error?.Category);
            Assert.Equal(code, result.Error?.Code);
            Assert.Equal(disposition, result.Error?.Disposition);
            Assert.Equal("synthetic-openrouter-error-request", result.Error?.RequestId);
            Assert.Equal(
                status == 429 ? TimeSpan.FromSeconds(61) : null,
                result.Error?.SuggestedDelay);
            AssertErrorIsRedacted(result);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task RequiredZdrUnavailableReturnsRedactedAttentionState()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            $$"""
            { "error": { "message": "{{SyntheticProviderBody}}" } }
            """,
            requestId: "synthetic-openrouter-zdr-request"));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.test/api/v1/")
        };
        var engine = new OpenRouterTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)),
            new OpenRouterTranscriptionOptions(RequireZeroDataRetention: true));
        var inputPath = CreateAudioFixture(".ogg", [0x4f, 0x67, 0x67, 0x53]);

        try
        {
            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(
                    Guid.NewGuid(),
                    inputPath,
                    ModelId: "openai/whisper-large-v3-turbo"),
                progress: null,
                CancellationToken.None);

            Assert.Equal(TranscriptionErrorCategory.Configuration, result.Error?.Category);
            Assert.Equal("zdr_route_unavailable", result.Error?.Code);
            Assert.Equal(TranscriptionFailureDisposition.AttentionRequired, result.Error?.Disposition);
            Assert.Equal("synthetic-openrouter-zdr-request", result.Error?.RequestId);
            AssertErrorIsRedacted(result);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task RequiredZdrDoesNotMaskInvalidRequestOrModelErrors(HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            statusCode,
            $$"""
            { "error": { "message": "{{SyntheticProviderBody}}" } }
            """));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.test/api/v1/")
        };
        var engine = new OpenRouterTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)),
            new OpenRouterTranscriptionOptions(RequireZeroDataRetention: true));
        var inputPath = CreateAudioFixture(".ogg", [0x4f, 0x67, 0x67, 0x53]);

        try
        {
            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(
                    Guid.NewGuid(),
                    inputPath,
                    ModelId: "openai/whisper-large-v3-turbo"),
                progress: null,
                CancellationToken.None);

            Assert.Equal(TranscriptionErrorCategory.Configuration, result.Error?.Category);
            Assert.Equal("invalid_engine_configuration", result.Error?.Code);
            Assert.Equal(TranscriptionFailureDisposition.AttentionRequired, result.Error?.Disposition);
            AssertErrorIsRedacted(result);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task OversizeInputRequestsSplitBeforeFileReadOrNetwork()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("Synthetic network handler must stay idle."));
        using var client = new HttpClient(handler);
        var engine = new OpenRouterTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)),
            new OpenRouterTranscriptionOptions(MaximumRawInputBytes: 4));
        var inputPath = CreateAudioFixture(".mp3", [0x49, 0x44, 0x33, 0x04, 0x05]);

        try
        {
            await using var exclusiveFile = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);

            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(
                    Guid.NewGuid(),
                    inputPath,
                    ModelId: "openai/whisper-large-v3-turbo"),
                progress: null,
                CancellationToken.None);

            Assert.Equal(TranscriptionErrorCategory.PayloadTooLarge, result.Error?.Category);
            Assert.Equal("payload_too_large", result.Error?.Code);
            Assert.Equal(TranscriptionFailureDisposition.SplitInput, result.Error?.Disposition);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task EncodedBodyBudgetRequestsSplitBeforeFileReadOrNetwork()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("Synthetic network handler must stay idle."));
        using var client = new HttpClient(handler);
        var engine = new OpenRouterTranscriptionEngine(
            client,
            new MemorySecretVault((RemoteTranscriptionSecrets.OpenRouter, SyntheticCredential)),
            new OpenRouterTranscriptionOptions(
                MaximumRawInputBytes: 100,
                MaximumEncodedRequestBytes: 64 * 1024));
        var inputPath = CreateAudioFixture(".mp3", [0x49, 0x44, 0x33, 0x04, 0x05]);

        try
        {
            await using var exclusiveFile = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);

            var result = await engine.TranscribeAsync(
                new TranscriptionRequest(
                    Guid.NewGuid(),
                    inputPath,
                    ModelId: "openai/whisper-large-v3-turbo"),
                progress: null,
                CancellationToken.None);

            Assert.Equal(TranscriptionErrorCategory.PayloadTooLarge, result.Error?.Category);
            Assert.Equal("payload_too_large", result.Error?.Code);
            Assert.Equal(TranscriptionFailureDisposition.SplitInput, result.Error?.Disposition);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private static void AssertErrorIsRedacted(TranscriptionResult result)
    {
        var error = JsonSerializer.Serialize(result.Error);
        Assert.DoesNotContain(SyntheticCredential, error, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticProviderBody, error, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string body,
        string? requestId = null,
        string requestIdHeader = "x-request-id")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation(requestIdHeader, requestId);
        }

        return response;
    }

    private static string CreateAudioFixture(string extension, byte[] bytes)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-openrouter-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public int RouteCheckCount { get; private set; }
        public string EndpointResponse { get; init; } = """{"data":{"endpoints":[{"tag":"groq"},{"tag":"deepinfra"}]}}""";
        public string ZdrResponse { get; init; } = """{"data":[{"model_id":"openai/whisper-large-v3-turbo","tag":"groq"},{"model_id":"openai/whisper-large-v3-turbo","tag":"deepinfra"}]}""";
        public int ZdrCheckCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? ContentType { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/endpoints/zdr", StringComparison.Ordinal))
            {
                ZdrCheckCount++;
                return JsonResponse(HttpStatusCode.OK, ZdrResponse);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/endpoints", StringComparison.Ordinal))
            {
                RouteCheckCount++;
                return JsonResponse(HttpStatusCode.OK, EndpointResponse);
            }
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
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

        public int ReadCount { get; private set; }

        public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(_values.GetValueOrDefault(key));
        }

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

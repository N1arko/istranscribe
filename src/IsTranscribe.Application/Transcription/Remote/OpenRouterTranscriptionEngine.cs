using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Transcription;
using TranscriptionOs = IsTranscribe.Core.Transcription.TranscriptionOperatingSystem;

namespace IsTranscribe.Application.Transcription.Remote;

public sealed record OpenRouterTranscriptionOptions(
    bool RequireZeroDataRetention = true,
    long MaximumRawInputBytes = 20L * 1024 * 1024,
    long MaximumEncodedRequestBytes = 28L * 1024 * 1024);

/// <summary>
/// OpenRouter speech-to-text adapter with explicit model discovery and bounded base64 input.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers.openrouter
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public sealed class OpenRouterTranscriptionEngine : ITranscriptionEngine, ITranscriptionModelDiscovery
{
    private const long EncodedRequestEnvelopeReserveBytes = 64L * 1024;
    public const string EngineId = "remote.openrouter";
    public const string DefaultModelId = "openai/whisper-large-v3-turbo";
    private const string LegacyReviewedModelId = "openai/whisper-1";

    private static readonly Uri DefaultBaseAddress = new("https://openrouter.ai/api/v1/");
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ISecretVault _secretVault;
    private readonly OpenRouterTranscriptionOptions _options;

    public OpenRouterTranscriptionEngine(
        HttpClient httpClient,
        ISecretVault secretVault,
        OpenRouterTranscriptionOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _options = options ?? new OpenRouterTranscriptionOptions();
        if (_options.MaximumRawInputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum raw input bytes must be positive.");
        }

        if (_options.MaximumEncodedRequestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum encoded request bytes must be positive.");
        }
    }

    public TranscriptionEngineCapabilities Capabilities { get; } = new(
        EngineId,
        "OpenRouter",
        TranscriptionExecutionKind.Remote,
        RequiresNetwork: true,
        PrivacyDisclosure: "Audio chunks are sent through OpenRouter and may incur provider charges.",
        SupportedPlatforms:
        [
            new TranscriptionPlatformTarget(TranscriptionOs.Windows, System.Runtime.InteropServices.Architecture.X64),
            new TranscriptionPlatformTarget(TranscriptionOs.MacOS, System.Runtime.InteropServices.Architecture.Arm64)
        ],
        Models: [],
        SupportedLanguageCodes: [],
        SupportsAutomaticLanguageDetection: true,
        SupportsDiarization: false,
        TimestampCapabilities: TranscriptionTimestampCapabilities.Segment | TranscriptionTimestampCapabilities.Word,
        SupportsModelDiscovery: true);

    public async ValueTask<IReadOnlyList<TranscriptionModelCapability>> DiscoverModelsAsync(
        CancellationToken cancellationToken)
    {
        var key = await _secretVault.ReadAsync(RemoteTranscriptionSecrets.OpenRouter, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("An OpenRouter credential is required for model discovery.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_httpClient.BaseAddress ?? DefaultBaseAddress, "models?output_modalities=transcription"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenRouter model discovery returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        using var document = await RemoteTranscriptionHttp.ReadBoundedJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("OpenRouter returned an invalid model catalog.");
        }

        var discovered = data.EnumerateArray()
            .Where(SupportsTranscription)
            .Select(static model => new
            {
                Id = ReadRequiredString(model, "id"),
                Name = ReadOptionalString(model, "name")
            })
            .Where(static model => model.Id == DefaultModelId)
            .DistinctBy(static model => model.Id, StringComparer.Ordinal)
            .OrderByDescending(static model => model.Id.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            .ThenBy(static model => model.Name ?? model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return discovered
            .Select((model, index) => new TranscriptionModelCapability(
                model.Id,
                string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name!,
                IsRecommended: index == 0))
            .ToArray();
    }

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            return Attention(
                TranscriptionErrorCategory.Configuration,
                "missing_model",
                "An OpenRouter transcription model must be selected.");
        }

        var key = await _secretVault.ReadAsync(RemoteTranscriptionSecrets.OpenRouter, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            return Attention(
                TranscriptionErrorCategory.Configuration,
                "missing_key",
                "An OpenRouter credential is required.");
        }

        if (!File.Exists(request.PrimaryAudioArtifactPath))
        {
            return Attention(
                TranscriptionErrorCategory.InvalidRequest,
                "input_missing",
                "The selected audio artifact is unavailable.");
        }

        long inputLength;
        try
        {
            inputLength = new FileInfo(request.PrimaryAudioArtifactPath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return InputUnreadable();
        }

        if (inputLength > _options.MaximumRawInputBytes
            || ExceedsEncodedRequestBudget(inputLength, _options.MaximumEncodedRequestBytes))
        {
            return PayloadTooLarge();
        }

        var format = RemoteTranscriptionHttp.NormalizeFormat(request.PrimaryAudioArtifactPath);
        if (format is not ("mp3" or "m4a" or "wav" or "flac" or "ogg"))
        {
            return Attention(
                TranscriptionErrorCategory.UnsupportedInput,
                "unsupported_input",
                "The audio format is unsupported by OpenRouter transcription.");
        }

        progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Preparing, 0));
        byte[]? audioBytes;
        try
        {
            audioBytes = await ReadBoundedInputAsync(
                    request.PrimaryAudioArtifactPath,
                    _options.MaximumRawInputBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return InputUnreadable();
        }

        if (audioBytes is null)
        {
            return PayloadTooLarge();
        }

        var requireZeroDataRetention = request.RequireZeroDataRetention
            ?? _options.RequireZeroDataRetention;
        var routeFailure = await VerifyWordTimestampRouteAsync(request.ModelId!, key, requireZeroDataRetention, cancellationToken).ConfigureAwait(false);
        if (routeFailure is not null) return routeFailure;
        var payload = new OpenRouterTranscriptionRequest(
            request.ModelId,
            new OpenRouterAudioInput(Convert.ToBase64String(audioBytes), format),
            string.Equals(request.Language, "auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : request.Language,
            Temperature: 0,
            Provider: new OpenRouterProviderPolicy(requireZeroDataRetention));
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        if (json.LongLength > _options.MaximumEncodedRequestBytes)
        {
            return PayloadTooLarge();
        }

        progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Uploading, 0));
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_httpClient.BaseAddress ?? DefaultBaseAddress, "audio/transcriptions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new ByteArrayContent(json);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };

        try
        {
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Processing, 0.5));
            if (!response.IsSuccessStatusCode)
            {
                if (requireZeroDataRetention
                    && response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    return TranscriptionResult.Failed(new TranscriptionError(
                        TranscriptionErrorCategory.Configuration,
                        "zdr_route_unavailable",
                        "The selected OpenRouter model has no available zero-retention route.",
                        requestId: RemoteTranscriptionHttp.ReadRequestId(response),
                        disposition: TranscriptionFailureDisposition.AttentionRequired));
                }

                return RemoteTranscriptionHttp.MapFailure(response, EngineId);
            }

            using var document = await RemoteTranscriptionHttp.ReadBoundedJsonAsync(response, cancellationToken)
                .ConfigureAwait(false);
            var result = MapCompleted(document.RootElement, request, response);
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Finalizing, 1));
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteTranscriptionHttp.NetworkFailure(
                "transport_timeout",
                "The OpenRouter request timed out.");
        }
        catch (HttpRequestException)
        {
            return RemoteTranscriptionHttp.NetworkFailure(
                "transport_error",
                "The OpenRouter request could not be completed.");
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
        catch (InvalidDataException)
        {
            return InvalidResponse();
        }
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers.openrouter
    private async ValueTask<TranscriptionResult?> VerifyWordTimestampRouteAsync(string model, string key, bool requireZeroDataRetention, CancellationToken cancellationToken)
    {
        if (model != DefaultModelId && model != LegacyReviewedModelId) return WordTimestampResponse.Missing();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(_httpClient.BaseAddress ?? DefaultBaseAddress, "models/" + model + "/endpoints"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return RemoteTranscriptionHttp.MapFailure(response, EngineId);
            using var document = await RemoteTranscriptionHttp.ReadBoundedJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("endpoints", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array
                || endpoints.GetArrayLength() == 0
                || endpoints.EnumerateArray().Any(endpoint => endpoint.ValueKind != JsonValueKind.Object
                    || !endpoint.TryGetProperty("tag", out var tag) || tag.ValueKind != JsonValueKind.String
                    || !(model == LegacyReviewedModelId ? tag.GetString() == "openai" : tag.GetString() is "groq" or "deepinfra")))
                return WordTimestampResponse.Missing();
            // STT ignores provider.only/order/ignore. Every possible endpoint must satisfy
            // the frozen privacy requirement before any audio bytes leave the device.
            if (requireZeroDataRetention)
            {
                using var policyRequest = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(_httpClient.BaseAddress ?? DefaultBaseAddress, "endpoints/zdr"));
                policyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var policyResponse = await _httpClient.SendAsync(policyRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!policyResponse.IsSuccessStatusCode) return RemoteTranscriptionHttp.MapFailure(policyResponse, EngineId);
                using var policies = await RemoteTranscriptionHttp.ReadBoundedJsonAsync(policyResponse, cancellationToken).ConfigureAwait(false);
                if (!policies.RootElement.TryGetProperty("data", out var allowed) || allowed.ValueKind != JsonValueKind.Array
                    || endpoints.EnumerateArray().Any(endpoint => !allowed.EnumerateArray().Any(policy =>
                        policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("model_id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == model
                        && policy.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String && tag.GetString() == endpoint.GetProperty("tag").GetString())))
                    return Attention(TranscriptionErrorCategory.Configuration, "zdr_route_unavailable",
                        "The selected speech service has no verified zero-retention route.");
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return RemoteTranscriptionHttp.NetworkFailure("transport_timeout", "The provider capability check timed out."); }
        catch (HttpRequestException)
        { return RemoteTranscriptionHttp.NetworkFailure("transport_error", "The provider capability check could not be completed."); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
        { return WordTimestampResponse.Missing(); }
    }

    private static TranscriptionResult MapCompleted(
        JsonElement root,
        TranscriptionRequest request,
        HttpResponseMessage response)
    {
        var text = ReadRequiredString(root, "text");
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException("Missing transcript text.");
        }

        var resolvedModel = ReadOptionalString(root, "model") ?? request.ModelId;
        var language = ReadOptionalString(root, "language");
        var segments = WordTimestampResponse.Read(root, text);
        if (segments is null) return WordTimestampResponse.Missing();
        var usage = root.TryGetProperty("usage", out var usageNode) && usageNode.ValueKind == JsonValueKind.Object
            ? ReadUsage(usageNode)
            : null;
        return TranscriptionResult.Completed(
            text,
            RemoteTranscriptionHttp.NormalizeDetectedLanguage(language),
            segments,
            metadata: new TranscriptionResultMetadata(
                ResolvedModelId: resolvedModel,
                RequestId: RemoteTranscriptionHttp.ReadRequestId(response),
                Usage: usage,
                SourceStart: request.SourceStart,
                SourceEnd: request.SourceEnd));
    }

    private static TranscriptionUsage? ReadUsage(JsonElement usage)
    {
        var input = ReadOptionalLong(usage, "prompt_tokens") ?? ReadOptionalLong(usage, "input_tokens");
        var output = ReadOptionalLong(usage, "completion_tokens") ?? ReadOptionalLong(usage, "output_tokens");
        var audioSeconds = ReadOptionalDouble(usage, "seconds");
        decimal? cost = null;
        if (usage.TryGetProperty("cost", out var costNode)
            && costNode.ValueKind == JsonValueKind.Number
            && costNode.TryGetDecimal(out var parsedCost)
            && parsedCost >= 0)
        {
            cost = parsedCost;
        }

        return input is null && output is null && audioSeconds is null && cost is null
            ? null
            : new TranscriptionUsage(
                InputUnits: input,
                OutputUnits: output,
                AudioSeconds: audioSeconds,
                ReportedCost: cost,
                Currency: cost is null ? null : "USD");
    }

    private static double? ReadOptionalDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var node)
        && node.ValueKind == JsonValueKind.Number
        && node.TryGetDouble(out var value)
        && double.IsFinite(value)
        && value >= 0
            ? value
            : null;

    private static long? ReadOptionalLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var node)
        && node.ValueKind == JsonValueKind.Number
        && node.TryGetInt64(out var value)
        && value >= 0
            ? value
            : null;

    private static string ReadRequiredString(JsonElement element, string propertyName) =>
        ReadOptionalString(element, propertyName) ?? string.Empty;

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static bool SupportsTranscription(JsonElement model)
    {
        if (ContainsCapability(model, "output_modalities", "transcription"))
        {
            return true;
        }

        foreach (var containerName in new[] { "architecture", "capabilities" })
        {
            if (!model.TryGetProperty(containerName, out var container)
                || container.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (ContainsCapability(container, "output_modalities", "transcription"))
            {
                return true;
            }

            var acceptsAudio = ContainsCapability(container, "input_modalities", "audio");
            var emitsText = ContainsCapability(container, "output_modalities", "text");
            if (acceptsAudio && emitsText)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsCapability(JsonElement container, string propertyName, string expected)
    {
        if (!container.TryGetProperty(propertyName, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return values.EnumerateArray().Any(value =>
            value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase));
    }

    private static TranscriptionResult Attention(
        TranscriptionErrorCategory category,
        string code,
        string message) =>
        TranscriptionResult.Failed(new TranscriptionError(
            category,
            code,
            message,
            disposition: TranscriptionFailureDisposition.AttentionRequired));

    private static TranscriptionResult InvalidResponse() =>
        TranscriptionResult.Failed(new TranscriptionError(
            TranscriptionErrorCategory.Processing,
            "invalid_response",
            "OpenRouter returned an invalid transcription response."));

    private static TranscriptionResult InputUnreadable() =>
        Attention(
            TranscriptionErrorCategory.UnsupportedInput,
            "input_unreadable",
            "The selected audio artifact cannot be read.");

    private static TranscriptionResult PayloadTooLarge() =>
        TranscriptionResult.Failed(new TranscriptionError(
            TranscriptionErrorCategory.PayloadTooLarge,
            "payload_too_large",
            "The audio chunk exceeds the OpenRouter upload budget.",
            disposition: TranscriptionFailureDisposition.SplitInput));

    private static bool ExceedsEncodedRequestBudget(long inputLength, long requestBudget)
    {
        var envelopeReserve = Math.Min(requestBudget, EncodedRequestEnvelopeReserveBytes);
        var availableBase64Bytes = requestBudget - envelopeReserve;
        var maximumEncodableInputBytes = (availableBase64Bytes / 4) * 3;
        return inputLength > maximumEncodableInputBytes;
    }

    private static async ValueTask<byte[]?> ReadBoundedInputAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream(capacity: checked((int)Math.Min(stream.Length, 1024 * 1024)));
        var block = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(block.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }

            buffer.Write(block, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed record OpenRouterTranscriptionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input_audio")] OpenRouterAudioInput InputAudio,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("provider")] OpenRouterProviderPolicy? Provider,
        [property: JsonPropertyName("response_format")] string ResponseFormat = "verbose_json",
        string[]? TimestampGranularities = null)
    {
        [JsonPropertyName("timestamp_granularities")]
        public string[] TimestampGranularities { get; init; } = TimestampGranularities ?? ["word", "segment"];
    }

    private sealed record OpenRouterAudioInput(
        [property: JsonPropertyName("data")] string Data,
        [property: JsonPropertyName("format")] string Format);

    // The STT endpoint ignores provider.only/order/ignore; never treat them as a routing guarantee.
    private sealed record OpenRouterProviderPolicy(
        [property: JsonPropertyName("zdr")] bool ZeroDataRetention);
}

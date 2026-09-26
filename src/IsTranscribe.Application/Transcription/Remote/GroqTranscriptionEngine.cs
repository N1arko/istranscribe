using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Transcription;
using TranscriptionOs = IsTranscribe.Core.Transcription.TranscriptionOperatingSystem;

namespace IsTranscribe.Application.Transcription.Remote;

/// <summary>
/// Groq Audio Transcriptions adapter. HTTP payloads remain internal to Application.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers.groq
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public sealed class GroqTranscriptionEngine : ITranscriptionEngine, ITranscriptionModelDiscovery
{
    public const string EngineId = "remote.groq";
    public const string DefaultModelId = "whisper-large-v3-turbo";
    public const string AccuracyModelId = "whisper-large-v3";
    private const long MaximumRawInputBytes = 20L * 1024 * 1024;

    private static readonly Uri DefaultBaseAddress = new("https://api.groq.com/openai/v1/");
    private readonly HttpClient _httpClient;
    private readonly ISecretVault _secretVault;

    public GroqTranscriptionEngine(HttpClient httpClient, ISecretVault secretVault)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    }

    public TranscriptionEngineCapabilities Capabilities { get; } = new(
        EngineId,
        "Groq",
        TranscriptionExecutionKind.Remote,
        RequiresNetwork: true,
        PrivacyDisclosure: "Audio chunks are sent to Groq for transcription and may incur provider charges.",
        SupportedPlatforms:
        [
            new TranscriptionPlatformTarget(TranscriptionOs.Windows, System.Runtime.InteropServices.Architecture.X64),
            new TranscriptionPlatformTarget(TranscriptionOs.MacOS, System.Runtime.InteropServices.Architecture.Arm64)
        ],
        Models:
        [
            new TranscriptionModelCapability(DefaultModelId, "Whisper Large v3 Turbo", IsRecommended: true)
        ],
        SupportedLanguageCodes: [],
        SupportsAutomaticLanguageDetection: true,
        SupportsDiarization: false,
        TimestampCapabilities: TranscriptionTimestampCapabilities.Segment | TranscriptionTimestampCapabilities.Word,
        SupportsModelDiscovery: true);

    public async ValueTask<IReadOnlyList<TranscriptionModelCapability>> DiscoverModelsAsync(
        CancellationToken cancellationToken)
    {
        var key = await _secretVault.ReadAsync(RemoteTranscriptionSecrets.Groq, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("A Groq credential is required for model discovery.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_httpClient.BaseAddress ?? DefaultBaseAddress, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Groq model discovery returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        using var document = await RemoteTranscriptionHttp.ReadBoundedJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Groq returned an invalid model catalog.");
        }

        var available = data.EnumerateArray()
            .Where(static model => model.TryGetProperty("id", out var id)
                                   && id.ValueKind == JsonValueKind.String)
            .Select(static model => model.GetProperty("id").GetString())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        return Capabilities.Models.Where(model => available.Contains(model.Id)).ToArray();
    }

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var key = await _secretVault.ReadAsync(RemoteTranscriptionSecrets.Groq, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            return TranscriptionResult.Failed(new TranscriptionError(
                TranscriptionErrorCategory.Configuration,
                "missing_key",
                "A Groq credential is required.",
                disposition: TranscriptionFailureDisposition.AttentionRequired));
        }

        if (!File.Exists(request.PrimaryAudioArtifactPath))
        {
            return TranscriptionResult.Failed(new TranscriptionError(
                TranscriptionErrorCategory.InvalidRequest,
                "input_missing",
                "The selected audio artifact is unavailable.",
                disposition: TranscriptionFailureDisposition.AttentionRequired));
        }

        long inputLength;
        FileStream input;
        try
        {
            inputLength = new FileInfo(request.PrimaryAudioArtifactPath).Length;
            input = File.OpenRead(request.PrimaryAudioArtifactPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return InputUnreadable();
        }

        await using var ownedInput = input;
        if (inputLength > MaximumRawInputBytes)
        {
            return TranscriptionResult.Failed(new TranscriptionError(
                TranscriptionErrorCategory.PayloadTooLarge,
                "payload_too_large",
                "The audio chunk exceeds the Groq upload budget.",
                disposition: TranscriptionFailureDisposition.SplitInput));
        }

        progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Uploading, 0));
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_httpClient.BaseAddress ?? DefaultBaseAddress, "audio/transcriptions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var multipart = new MultipartFormDataContent();
        using var file = new StreamContent(ownedInput);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(file, "file", Path.GetFileName(request.PrimaryAudioArtifactPath));
        multipart.Add(new StringContent(request.ModelId ?? DefaultModelId), "model");
        multipart.Add(new StringContent("verbose_json"), "response_format");
        multipart.Add(new StringContent("0"), "temperature");
        multipart.Add(new StringContent("segment"), "timestamp_granularities[]");
        multipart.Add(new StringContent("word"), "timestamp_granularities[]");
        if (!string.IsNullOrWhiteSpace(request.Language)
            && !string.Equals(request.Language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            multipart.Add(new StringContent(request.Language), "language");
        }

        message.Content = multipart;

        try
        {
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Processing, 0.5));
            if (!response.IsSuccessStatusCode)
            {
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
                "The Groq request timed out.");
        }
        catch (HttpRequestException)
        {
            return RemoteTranscriptionHttp.NetworkFailure(
                "transport_error",
                "The Groq request could not be completed.");
        }
        catch (JsonException)
        {
            return TranscriptionResult.Failed(new TranscriptionError(
                TranscriptionErrorCategory.Processing,
                "invalid_response",
                "Groq returned an invalid transcription response."));
        }
        catch (InvalidDataException)
        {
            return TranscriptionResult.Failed(new TranscriptionError(
                TranscriptionErrorCategory.Processing,
                "invalid_response",
                "Groq returned an invalid transcription response."));
        }
    }

    private static TranscriptionResult MapCompleted(
        JsonElement root,
        TranscriptionRequest request,
        HttpResponseMessage response)
    {
        var text = root.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String
            ? textNode.GetString() ?? string.Empty
            : throw new JsonException("Missing transcript text.");
        var language = root.TryGetProperty("language", out var languageNode)
            && languageNode.ValueKind == JsonValueKind.String
            ? languageNode.GetString()
            : null;
        var duration = TryReadFiniteDouble(root, "duration");
        var segments = WordTimestampResponse.Read(root, text);
        if (segments is null) return WordTimestampResponse.Missing();
        var metadata = new TranscriptionResultMetadata(
            ResolvedModelId: request.ModelId ?? DefaultModelId,
            RequestId: RemoteTranscriptionHttp.ReadRequestId(response),
            Usage: duration is { } seconds ? new TranscriptionUsage(AudioSeconds: seconds) : null,
            SourceStart: request.SourceStart,
            SourceEnd: request.SourceEnd,
            AudioDuration: duration is { } value ? TimeSpan.FromSeconds(value) : null);
        return TranscriptionResult.Completed(text, RemoteTranscriptionHttp.NormalizeDetectedLanguage(language), segments, metadata);
    }

    private static TranscriptionResult InputUnreadable() =>
        TranscriptionResult.Failed(new TranscriptionError(
            TranscriptionErrorCategory.UnsupportedInput,
            "input_unreadable",
            "The selected audio artifact cannot be read.",
            disposition: TranscriptionFailureDisposition.AttentionRequired));

    private static IReadOnlyList<TranscriptionSegment> ReadSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segmentsNode)
            || segmentsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var segments = new List<TranscriptionSegment>();
        foreach (var item in segmentsNode.EnumerateArray())
        {
            if (!item.TryGetProperty("text", out var textNode)
                || textNode.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(textNode.GetString()))
            {
                continue;
            }

            var start = TryReadFiniteDouble(item, "start");
            var end = TryReadFiniteDouble(item, "end");
            segments.Add(new TranscriptionSegment(
                textNode.GetString()!,
                start is { } startSeconds ? TimeSpan.FromSeconds(Math.Max(0, startSeconds)) : null,
                end is { } endSeconds ? TimeSpan.FromSeconds(Math.Max(0, endSeconds)) : null));
        }

        return segments;
    }

    private static double? TryReadFiniteDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node)
            || node.ValueKind != JsonValueKind.Number
            || !node.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            return null;
        }

        return value;
    }
}

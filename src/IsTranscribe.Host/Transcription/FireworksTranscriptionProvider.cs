using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IsTranscribe.Host.Transcription;

public sealed class FireworksTranscriptionProvider(HttpClient httpClient) : ITranscriptionProvider
{
    private readonly HttpClient _httpClient = httpClient;

    // @spec spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#request-contract.http
    // @spec spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#request-contract.response
    public async ValueTask<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, string apiKey, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.AudioPath))
        {
            throw new TranscriptionProviderException(
                errorCode: "TX_INPUT_MISSING",
                message: "Audio artifact for transcription was not found.",
                kind: TranscriptionFailureKind.Terminal);
        }

        using var form = new MultipartFormDataContent();
        await using var stream = File.Open(request.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var audioContent = new StreamContent(stream);
        var contentType = Path.GetExtension(request.AudioPath).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? "audio/ogg"
            : "audio/wav";
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(audioContent, "file", Path.GetFileName(request.AudioPath));
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");

        if (!string.IsNullOrWhiteSpace(request.Language) && !string.Equals(request.Language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(request.Language, Encoding.UTF8), "language");
        }

        if (request.DiarizationEnabled)
        {
            form.Add(new StringContent("verbose_json", Encoding.UTF8), "response_format");
            form.Add(new StringContent("word", Encoding.UTF8), "timestamp_granularities[]");
            form.Add(new StringContent("true", Encoding.UTF8), "diarization");
            form.Add(new StringContent(request.MinSpeakers.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8), "min_speakers");
            form.Add(new StringContent(request.MaxSpeakers.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8), "max_speakers");
        }
        else
        {
            form.Add(new StringContent("json", Encoding.UTF8), "response_format");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = form;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception)
        {
            throw new TranscriptionProviderException("TX_TIMEOUT", "Transcription request timed out.", TranscriptionFailureKind.Transient, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionProviderException("TX_NETWORK", "Transcription request failed due to network error.", TranscriptionFailureKind.Transient, exception);
        }

        try
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rawJson = document.RootElement.GetRawText();

            if (!response.IsSuccessStatusCode)
            {
                throw ClassifyNonSuccess(response.StatusCode, rawJson);
            }

            var text = document.RootElement.TryGetProperty("text", out var textElement)
                ? textElement.GetString() ?? string.Empty
                : string.Empty;

            var hasSpeakerMetadata = rawJson.Contains("speaker", StringComparison.OrdinalIgnoreCase);
            return new TranscriptionResponse(text, rawJson, hasSpeakerMetadata);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static TranscriptionProviderException ClassifyNonSuccess(HttpStatusCode statusCode, string payload)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new TranscriptionProviderException("TX_INVALID_API_KEY", BuildMessage(statusCode, payload), TranscriptionFailureKind.Configuration);
        }

        if ((int)statusCode == 429 || (int)statusCode >= 500)
        {
            return new TranscriptionProviderException("TX_PROVIDER_UNAVAILABLE", BuildMessage(statusCode, payload), TranscriptionFailureKind.Transient);
        }

        return new TranscriptionProviderException("TX_PROVIDER_REJECTED", BuildMessage(statusCode, payload), TranscriptionFailureKind.Terminal);
    }

    private static string BuildMessage(HttpStatusCode statusCode, string payload)
    {
        var compactPayload = payload.Length <= 300 ? payload : payload[..300];
        return $"Provider returned {(int)statusCode} ({statusCode}). Payload: {compactPayload}";
    }
}

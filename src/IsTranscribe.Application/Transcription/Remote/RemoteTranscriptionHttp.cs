using System.Globalization;
using System.Net;
using System.Text.Json;
using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Application.Transcription.Remote;

/// <summary>
/// Applies bounded, redacted HTTP handling shared by the remote transcription engines.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
internal static class RemoteTranscriptionHttp
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;

    // Providers may return an English language name instead of the ISO code used locally.
    // Use the platform language catalog, independently from the selected UI culture.
    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#recognition
    public static string? NormalizeDetectedLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var value = language.Trim();
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(candidate =>
            candidate.Name.Length > 0 && (string.Equals(candidate.EnglishName, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.TwoLetterISOLanguageName, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ThreeLetterISOLanguageName, value, StringComparison.OrdinalIgnoreCase)));
        if (culture is not null) return culture.TwoLetterISOLanguageName.ToLowerInvariant();
        // Preserve new ISO-shaped codes without a product-level language allowlist.
        return value.Length is 2 or 3 && value.All(char.IsAsciiLetter) ? value.ToLowerInvariant() : null;
    }

    public static async ValueTask<JsonDocument> ReadBoundedJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The transcription response exceeded the allowed size.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(block.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("The transcription response exceeded the allowed size.");
            }

            buffer.Write(block, 0, read);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static TranscriptionResult MapFailure(HttpResponseMessage response, string engineId)
    {
        var statusCode = (int)response.StatusCode;
        var requestId = ReadRequestId(response);
        var suggestedDelay = ReadSuggestedDelay(response);
        var (category, code, disposition) = statusCode switch
        {
            401 or 403 => (
                TranscriptionErrorCategory.Authentication,
                "invalid_key",
                TranscriptionFailureDisposition.AttentionRequired),
            402 => (
                TranscriptionErrorCategory.PaymentRequired,
                "insufficient_credit",
                TranscriptionFailureDisposition.AttentionRequired),
            413 => (
                TranscriptionErrorCategory.PayloadTooLarge,
                "payload_too_large",
                TranscriptionFailureDisposition.SplitInput),
            429 or 498 => (
                TranscriptionErrorCategory.RateLimited,
                "rate_limited",
                TranscriptionFailureDisposition.TryAgain),
            >= 500 => (
                TranscriptionErrorCategory.EngineUnavailable,
                "engine_unavailable",
                TranscriptionFailureDisposition.TryAgain),
            400 or 404 or 422 => (
                TranscriptionErrorCategory.Configuration,
                "invalid_engine_configuration",
                TranscriptionFailureDisposition.AttentionRequired),
            _ => (
                TranscriptionErrorCategory.Processing,
                "engine_request_failed",
                TranscriptionFailureDisposition.Terminal)
        };

        return TranscriptionResult.Failed(new TranscriptionError(
            category,
            code,
            $"{engineId} returned HTTP {statusCode.ToString(CultureInfo.InvariantCulture)}.",
            suggestedDelay,
            requestId,
            disposition));
    }

    public static TranscriptionResult NetworkFailure(string code, string message) =>
        TranscriptionResult.Failed(new TranscriptionError(
            TranscriptionErrorCategory.Network,
            code,
            message,
            disposition: TranscriptionFailureDisposition.TryAgain));

    public static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var headerName in new[] { "x-generation-id", "x-request-id", "request-id", "cf-ray" })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            }
        }

        return null;
    }

    private static TimeSpan? ReadSuggestedDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    public static string NormalizeFormat(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "mp3" => "mp3",
            "m4a" or "mp4" => "m4a",
            "wav" => "wav",
            "flac" => "flac",
            "ogg" or "oga" or "opus" => "ogg",
            _ => extension
        };
    }
}

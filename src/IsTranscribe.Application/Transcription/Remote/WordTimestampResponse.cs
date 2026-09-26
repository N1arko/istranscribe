using System.Text.Json;
using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Application.Transcription.Remote;

/// <summary>Validates the word timing contract before any speaker assignment.</summary>
/// <remarks>@spec spec://modules/app/FEAT-017-speaker-aware-transcription#recognition</remarks>
internal static class WordTimestampResponse
{
    public static IReadOnlyList<TranscriptionSegment>? Read(JsonElement root, string text)
    {
        if (!root.TryGetProperty("words", out var array) || array.ValueKind != JsonValueKind.Array)
            return string.IsNullOrWhiteSpace(text) ? [] : null;

        var words = new List<TranscriptionWord>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("word", out var word) || word.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(word.GetString())
                || !Seconds(item, "start", out var start) || !Seconds(item, "end", out var end)
                || end < start || (words.Count > 0 && start < words[^1].Start.TotalSeconds))
                return null;
            words.Add(new TranscriptionWord(word.GetString()!, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
        }

        if (words.Count == 0) return string.IsNullOrWhiteSpace(text) ? [] : null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        return [new TranscriptionSegment(text, words[0].Start, words[^1].End, words: words)];
    }

    public static TranscriptionResult Missing() => TranscriptionResult.Failed(new TranscriptionError(
        TranscriptionErrorCategory.Configuration, "timestamp_capability_missing",
        "The selected speech service did not return valid word timestamps.",
        disposition: TranscriptionFailureDisposition.AttentionRequired));

    private static bool Seconds(JsonElement value, string key, out double seconds)
    {
        seconds = 0;
        return value.TryGetProperty(key, out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetDouble(out seconds) && double.IsFinite(seconds)
            && seconds >= 0 && seconds <= 86400;
    }
}

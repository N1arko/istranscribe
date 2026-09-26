using System.Text;
using System.Text.Json;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Validates the typed detection decision log and scans decoded values for raw
/// titles or payload-like audio data before a log may enter durable evidence.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// </remarks>
internal static class LogPrivacyInspector
{
    private static readonly HashSet<string> AllowedRootProperties = new(StringComparer.Ordinal)
    {
        "timestamp_utc",
        "level",
        "event_code",
        "session_id",
        "job_name",
        "message",
        "metadata"
    };

    private static readonly HashSet<string> AllowedDetectionMetadataProperties = new(StringComparer.Ordinal)
    {
        "candidate_count",
        "candidates",
        "candidates_truncated",
        "prompt_visible",
        "prompt_candidate_id",
        "shadow_would_prompt_candidate_id",
        "degraded_signals"
    };

    private static readonly HashSet<string> AllowedCandidateProperties = new(StringComparer.Ordinal)
    {
        "candidate_id",
        "profile_id",
        "score",
        "band",
        "suppressed",
        "reason_codes",
        "evidence",
        "contributions"
    };

    private static readonly HashSet<string> AllowedEvidenceProperties = new(StringComparer.Ordinal)
    {
        "kind",
        "rule_id",
        "provider_id",
        "strength"
    };

    private static readonly HashSet<string> AllowedContributionProperties = new(StringComparer.Ordinal)
    {
        "kind",
        "rule_id",
        "strength",
        "weight",
        "score_delta"
    };

    private static readonly string[] AudioPayloadPropertyFragments =
    [
        "audio_payload",
        "audio_bytes",
        "raw_audio",
        "pcm",
        "samples",
        "waveform"
    ];

    public static PrivacyInspectionResult Inspect(
        string logPath,
        IReadOnlyList<string> rawWindowTitles,
        ExpectedPromptLogEvidence? expectedPrompt)
    {
        if (!File.Exists(logPath))
        {
            return new PrivacyInspectionResult(false, 0, 0);
        }

        try
        {
            var normalizedTitles = rawWindowTitles
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .Select(NormalizeText)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var schemaValid = true;
            var sawDetectionDecision = false;
            var sawExpectedPromptDecision = false;
            var rawTitleMatchCount = 0;
            var audioPayloadFieldCount = 0;
            foreach (var rawLine in File.ReadLines(logPath))
            {
                var line = rawLine.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    schemaValid &= HasValidRootSchema(root);
                    rawTitleMatchCount += CountDecodedTitleMatches(root, normalizedTitles);
                    audioPayloadFieldCount += CountPotentialAudioPayload(root);
                    if (root.TryGetProperty("event_code", out var eventCode)
                        && string.Equals(
                            eventCode.GetString(),
                            "DETECTION_DECISION",
                            StringComparison.Ordinal))
                    {
                        sawDetectionDecision = true;
                        schemaValid &= ValidateDetectionDecision(
                            root,
                            expectedPrompt,
                            ref sawExpectedPromptDecision);
                    }
                }
                catch (JsonException)
                {
                    schemaValid = false;
                }
            }

            var requiredDecisionObserved = expectedPrompt is null
                ? sawDetectionDecision
                : sawExpectedPromptDecision;
            return new PrivacyInspectionResult(
                schemaValid && requiredDecisionObserved,
                rawTitleMatchCount,
                audioPayloadFieldCount);
        }
        catch (IOException)
        {
            return new PrivacyInspectionResult(false, 0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new PrivacyInspectionResult(false, 0, 0);
        }
    }

    private static bool HasValidRootSchema(JsonElement root) =>
        HasOnlyProperties(root, AllowedRootProperties)
        && TryReadString(root, "timestamp_utc", out var timestamp)
        && DateTimeOffset.TryParse(timestamp, out _)
        && TryReadString(root, "level", out _)
        && TryReadString(root, "event_code", out _)
        && TryReadString(root, "message", out _)
        && root.TryGetProperty("metadata", out var metadata)
        && metadata.ValueKind == JsonValueKind.Object;

    private static bool ValidateDetectionDecision(
        JsonElement root,
        ExpectedPromptLogEvidence? expectedPrompt,
        ref bool sawExpectedPromptDecision)
    {
        if (!root.TryGetProperty("metadata", out var metadata)
            || !HasOnlyProperties(metadata, AllowedDetectionMetadataProperties)
            || !TryReadInt32(metadata, "candidate_count", out var candidateCount)
            || candidateCount < 0
            || !metadata.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || !TryReadBoolean(metadata, "candidates_truncated", out var candidatesTruncated)
            || !TryReadBoolean(metadata, "prompt_visible", out var promptVisible)
            || !IsNullOrSafeToken(metadata, "prompt_candidate_id")
            || !IsNullOrSafeToken(metadata, "shadow_would_prompt_candidate_id")
            || !ValidateStringArray(metadata, "degraded_signals"))
        {
            return false;
        }

        var candidateElements = candidates.EnumerateArray().ToArray();
        if (candidatesTruncated
                ? candidateElements.Length > candidateCount
                : candidateElements.Length != candidateCount)
        {
            return false;
        }

        var promptCandidateId = metadata.GetProperty("prompt_candidate_id").ValueKind == JsonValueKind.String
            ? metadata.GetProperty("prompt_candidate_id").GetString()
            : null;
        foreach (var candidate in candidateElements)
        {
            if (!ValidateCandidate(candidate))
            {
                return false;
            }

            if (expectedPrompt is not null
                && promptVisible
                && string.Equals(promptCandidateId, expectedPrompt.CandidateId, StringComparison.Ordinal)
                && CandidateMatchesExpectedPrompt(candidate, expectedPrompt))
            {
                sawExpectedPromptDecision = true;
            }
        }

        return !promptVisible || !string.IsNullOrWhiteSpace(promptCandidateId);
    }

    private static bool ValidateCandidate(JsonElement candidate)
    {
        if (!HasOnlyProperties(candidate, AllowedCandidateProperties)
            || !TryReadSafeToken(candidate, "candidate_id", out _)
            || !TryReadSafeToken(candidate, "profile_id", out _)
            || !TryReadInt32(candidate, "score", out var score)
            || score is < 0 or > 100
            || !TryReadString(candidate, "band", out var band)
            || band is not ("ignored" or "suspected" or "ask")
            || !TryReadBoolean(candidate, "suppressed", out _)
            || !ValidateStringArray(candidate, "reason_codes")
            || !candidate.TryGetProperty("evidence", out var evidence)
            || evidence.ValueKind != JsonValueKind.Array
            || !candidate.TryGetProperty("contributions", out var contributions)
            || contributions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return evidence.EnumerateArray().All(ValidateEvidence)
            && contributions.EnumerateArray().All(ValidateContribution);
    }

    private static bool CandidateMatchesExpectedPrompt(
        JsonElement candidate,
        ExpectedPromptLogEvidence expected)
    {
        return candidate.GetProperty("candidate_id").GetString() == expected.CandidateId
            && candidate.GetProperty("profile_id").GetString() == expected.ProfileId
            && candidate.GetProperty("score").GetInt32() == expected.Score
            && candidate.GetProperty("band").GetString() == "ask"
            && candidate.GetProperty("reason_codes").GetArrayLength() > 0
            && candidate.GetProperty("evidence").GetArrayLength() > 0
            && candidate.GetProperty("contributions").GetArrayLength() > 0;
    }

    private static bool ValidateEvidence(JsonElement evidence) =>
        HasOnlyProperties(evidence, AllowedEvidenceProperties)
        && TryReadString(evidence, "kind", out _)
        && TryReadSafeToken(evidence, "rule_id", out _)
        && IsNullOrSafeToken(evidence, "provider_id")
        && TryReadDouble(evidence, "strength", out var strength)
        && strength is >= 0 and <= 1;

    private static bool ValidateContribution(JsonElement contribution) =>
        HasOnlyProperties(contribution, AllowedContributionProperties)
        && TryReadString(contribution, "kind", out _)
        && TryReadSafeToken(contribution, "rule_id", out _)
        && TryReadDouble(contribution, "strength", out var strength)
        && strength is >= 0 and <= 1
        && TryReadInt32(contribution, "weight", out var weight)
        && weight is >= -100 and <= 100
        && TryReadInt32(contribution, "score_delta", out var delta)
        && delta is >= -100 and <= 100;

    private static bool ValidateStringArray(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var values)
        && values.ValueKind == JsonValueKind.Array
        && values.EnumerateArray().All(static value =>
            value.ValueKind == JsonValueKind.String && IsSafeToken(value.GetString()));

    private static bool IsNullOrSafeToken(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && (value.ValueKind == JsonValueKind.Null
            || (value.ValueKind == JsonValueKind.String && IsSafeToken(value.GetString())));

    private static bool TryReadSafeToken(JsonElement parent, string propertyName, out string value)
    {
        if (TryReadString(parent, propertyName, out value) && IsSafeToken(value))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
        {
            return false;
        }

        return value.All(static character => character is
            >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or
            '.' or '_' or '-' or ':');
    }

    private static bool TryReadString(JsonElement parent, string propertyName, out string value)
    {
        if (parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadBoolean(JsonElement parent, string propertyName, out bool value)
    {
        if (parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryReadInt32(JsonElement parent, string propertyName, out int value)
    {
        if (parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadDouble(JsonElement parent, string propertyName, out double value)
    {
        if (parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool HasOnlyProperties(JsonElement element, IReadOnlySet<string> allowedProperties) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().All(property => allowedProperties.Contains(property.Name));

    private static int CountDecodedTitleMatches(
        JsonElement element,
        IReadOnlyList<string> normalizedTitles)
    {
        var count = 0;
        if (element.ValueKind == JsonValueKind.String)
        {
            var decoded = NormalizeText(element.GetString() ?? string.Empty);
            count += normalizedTitles.Count(title =>
                decoded.Contains(title, StringComparison.OrdinalIgnoreCase));
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            count += element.EnumerateObject().Sum(property =>
                CountDecodedTitleMatches(property.Value, normalizedTitles));
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            count += element.EnumerateArray().Sum(item =>
                CountDecodedTitleMatches(item, normalizedTitles));
        }

        return count;
    }

    private static int CountPotentialAudioPayload(JsonElement element)
    {
        var count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (AudioPayloadPropertyFragments.Any(fragment =>
                        property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                {
                    count++;
                }

                count += CountPotentialAudioPayload(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            if (items.Length >= 32 && items.All(static item => item.ValueKind == JsonValueKind.Number))
            {
                count++;
            }

            count += items.Sum(CountPotentialAudioPayload);
        }
        else if (element.ValueKind == JsonValueKind.String
            && LooksLikeEncodedPayload(element.GetString()))
        {
            count++;
        }

        return count;
    }

    private static bool LooksLikeEncodedPayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 256)
        {
            return false;
        }

        var compact = value.Where(static character => !char.IsWhiteSpace(character)).ToArray();
        return compact.Length >= 256
            && compact.Count(static character => char.IsLetterOrDigit(character) || character is '+' or '/' or '=')
                >= compact.Length * 0.98;
    }

    private static string NormalizeText(string value) => value.Normalize(NormalizationForm.FormKC);
}

internal sealed record ExpectedPromptLogEvidence(
    string CandidateId,
    string ProfileId,
    int Score);

internal sealed record PrivacyInspectionResult(
    bool DetectionLogSchemaValid,
    int RawWindowTitleMatchCount,
    int AudioPayloadFieldCount);

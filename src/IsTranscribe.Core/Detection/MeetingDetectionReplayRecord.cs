using System.Text.Json.Serialization;

namespace IsTranscribe.Core.Detection;

/// <summary>
/// Privacy-reduced, versioned candidate frame that can be read back from local
/// diagnostics and passed through the production scorer/replay lifecycle.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public sealed record MeetingDetectionReplayRecord(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("observed_at_utc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("profile_id")] string ProfileId,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("evidence")] IReadOnlyList<MeetingDetectionReplayEvidenceRecord> Evidence,
    [property: JsonPropertyName("evidence_weight_overrides")] IReadOnlyDictionary<string, int> EvidenceWeightOverrides,
    [property: JsonPropertyName("recorded_score")] int RecordedScore,
    [property: JsonPropertyName("recorded_band")] string RecordedBand,
    [property: JsonPropertyName("suppressed")] bool Suppressed)
{
    public const int CurrentSchemaVersion = 1;

    public MeetingObservationFrame ToFrame()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported replay schema version: {SchemaVersion}.");
        }

        var context = ParseEnumToken<MeetingCandidateContext>(Context, nameof(Context));
        var evidence = Evidence
            .Select(item => new MeetingEvidenceFact(
                ParseEnumToken<MeetingEvidenceKind>(item.Kind, nameof(item.Kind)),
                item.RuleId,
                item.Strength,
                item.ProviderId ?? "replay"))
            .ToArray();
        var weights = EvidenceWeightOverrides.ToDictionary(
            pair => ParseEnumToken<MeetingEvidenceKind>(pair.Key, "evidence_weight_overrides"),
            static pair => pair.Value);
        return new MeetingObservationFrame(
            ObservedAtUtc,
            CandidateId,
            ProfileId,
            ProfileId,
            context,
            RootProcessId: 0,
            ProcessName: "replay",
            evidence,
            weights);
    }

    private static TEnum ParseEnumToken<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum
    {
        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(
                name.Replace("_", string.Empty, StringComparison.Ordinal),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        throw new InvalidDataException($"Unknown {fieldName} token: {value}.");
    }
}

public sealed record MeetingDetectionReplayEvidenceRecord(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("strength")] double Strength,
    [property: JsonPropertyName("provider_id")] string? ProviderId);

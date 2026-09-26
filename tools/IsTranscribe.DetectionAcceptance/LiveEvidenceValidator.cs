namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Enforces that a passing artifact proves every release gate instead of merely
/// describing a successful-looking prompt.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// </remarks>
public static class LiveEvidenceValidator
{
    public const string SchemaVersion = "feat-011-live-v1";
    public const long ReleaseAskLimitMilliseconds = 15_000;

    public static IReadOnlyList<string> Validate(LiveEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var errors = new List<string>();

        Require(
            string.Equals(evidence.SchemaVersion, SchemaVersion, StringComparison.Ordinal),
            "schema_version must be feat-011-live-v1.",
            errors);
        Require(!string.IsNullOrWhiteSpace(evidence.RunId), "run_id is required.", errors);
        Require(!string.IsNullOrWhiteSpace(evidence.ScenarioId), "scenario_id is required.", errors);
        Require(!string.IsNullOrWhiteSpace(evidence.Subject.ProfileId), "subject.profile_id is required.", errors);
        Require(
            evidence.Subject.Surface is "desktop" or "browser",
            "subject.surface must be desktop or browser.",
            errors);

        if (evidence.Outcome.Status != RunOutcomeStatus.Passed)
        {
            Require(
                !string.IsNullOrWhiteSpace(evidence.Outcome.ReasonCode),
                "An incomplete or failed run requires outcome.reason_code.",
                errors);
            return errors;
        }

        Require(evidence.Outcome.ReasonCode is null, "A passed run cannot have a reason code.", errors);
        Require(
            string.Equals(evidence.Target.Architecture, "x64", StringComparison.OrdinalIgnoreCase),
            "A passed Windows-first run must execute as x64.",
            errors);
        Require(
            IsSha256(evidence.Build.RuntimeAssemblySha256)
            && IsSha256(evidence.Build.RunnerAssemblySha256)
            && HasRequiredDependencies(evidence.Build.DependencySha256)
            && evidence.Build.DependencySha256.TryGetValue(
                "IsTranscribe.Platform.Windows.dll",
                out var runtimeDependencyHash)
            && string.Equals(
                evidence.Build.RuntimeAssemblySha256,
                runtimeDependencyHash,
                StringComparison.Ordinal)
            && evidence.Build.DependencySha256.TryGetValue(
                "IsTranscribe.DetectionAcceptance.dll",
                out var runnerDependencyHash)
            && string.Equals(
                evidence.Build.RunnerAssemblySha256,
                runnerDependencyHash,
                StringComparison.Ordinal),
            "A passed run requires the complete first-party and schema build manifest.",
            errors);
        Require(
            evidence.Subject.Client.Version.Status == ObservationValueStatus.Observed
            && !string.IsNullOrWhiteSpace(evidence.Subject.Client.Version.Value),
            "A passed run requires an observed client version.",
            errors);
        Require(
            !string.IsNullOrWhiteSpace(evidence.Subject.Client.ExecutableSha256),
            "A passed run requires a client executable hash.",
            errors);
        Require(
            evidence.Observation.ActivityReady.Status == CheckStatus.Pass
            && string.Equals(
                evidence.Observation.ActivityReady.Source,
                "operator_confirmation",
                StringComparison.Ordinal)
            && evidence.Observation.ActivityReady.AtUtc.HasValue,
            "A passed run requires timestamped operator confirmation of meaningful activity.",
            errors);
        Require(
            evidence.Observation.Ask.Status == CheckStatus.Pass
            && evidence.Observation.Ask.ElapsedMilliseconds is >= 0
            && evidence.Observation.Ask.ElapsedMilliseconds <= ReleaseAskLimitMilliseconds
            && evidence.Observation.Ask.LimitMilliseconds == ReleaseAskLimitMilliseconds,
            "A passed run requires Ask within the fixed 15-second release limit.",
            errors);
        Require(
            evidence.Observation.Prompt.Status == CheckStatus.Pass
            && evidence.Observation.Prompt.Published == true
            && string.Equals(
                evidence.Observation.Prompt.ObservedProfileId,
                evidence.Subject.ProfileId,
                StringComparison.OrdinalIgnoreCase)
            && CandidateMatchesSurface(
                evidence.Observation.Prompt.CandidateId,
                evidence.Subject.Surface)
            && CandidateMatchesProcess(
                evidence.Observation.Prompt.CandidateId,
                evidence.Subject.Client.ProcessId)
            && string.Equals(
                evidence.Observation.Prompt.ActivityState,
                "awaiting_confirmation",
                StringComparison.Ordinal)
            && evidence.Observation.Prompt.ConfidenceScore is >= 0 and <= 100,
            "A passed run requires a published, matching Ask prompt and confidence score.",
            errors);
        Require(
            string.Equals(
                evidence.ScenarioId,
                $"{evidence.Subject.ProfileId}.{evidence.Subject.Surface}.positive",
                StringComparison.Ordinal),
            "scenario_id must match the subject profile and surface.",
            errors);
        Require(
            evidence.Observation.PreconfirmationAudio.Status == CheckStatus.Pass
            && evidence.Observation.PreconfirmationAudio.CaptureStartCount == 0
            && evidence.Observation.PreconfirmationAudio.UnexpectedFileCount == 0
            && evidence.Observation.PreconfirmationAudio.MeetingSessionCount == 0
            && evidence.Observation.PreconfirmationAudio.ScannedRoots.Count >= 2,
            "A passed run requires zero pre-confirmation capture, files, and session rows.",
            errors);
        Require(
            evidence.Observation.Privacy.Status == CheckStatus.Pass
            && evidence.Observation.Privacy.DetectionLogSchemaValid == true
            && evidence.Observation.Privacy.RawWindowTitleMatchCount == 0
            && evidence.Observation.Privacy.AudioPayloadFieldCount == 0
            && evidence.Observation.Privacy.ManagedHttpRequestCount == 0,
            "A passed run requires every privacy boundary to be observed and clean.",
            errors);

        return errors;
    }

    private static bool CandidateMatchesSurface(string? candidateId, string surface) =>
        surface switch
        {
            "desktop" => candidateId?.Contains(":desktop:", StringComparison.Ordinal) == true,
            "browser" => candidateId?.StartsWith("browser:", StringComparison.Ordinal) == true,
            _ => false
        };

    private static bool CandidateMatchesProcess(string? candidateId, int? processId) =>
        processId.HasValue
        && TryReadRootProcessId(candidateId, out var candidateProcessId)
        && candidateProcessId == processId.Value;

    private static bool TryReadRootProcessId(string? candidateId, out int processId)
    {
        processId = 0;
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            return false;
        }

        var parts = candidateId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var value = candidateId.StartsWith("browser:", StringComparison.Ordinal)
            ? parts.ElementAtOrDefault(1)
            : parts.LastOrDefault();
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out processId);
    }

    private static bool HasRequiredDependencies(IReadOnlyDictionary<string, string> dependencies)
    {
        var required = new[]
        {
            "IsTranscribe.Core.dll",
            "IsTranscribe.Host.dll",
            "IsTranscribe.Persistence.dll",
            "IsTranscribe.Platform.Windows.dll",
            "IsTranscribe.DetectionAcceptance.dll",
            "feat-011-live-v1.schema.json"
        };
        return required.All(name =>
            dependencies.TryGetValue(name, out var hash) && IsSha256(hash));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Require(bool condition, string message, ICollection<string> errors)
    {
        if (!condition)
        {
            errors.Add(message);
        }
    }
}

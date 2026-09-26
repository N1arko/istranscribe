using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Platform.Windows;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Produces the consent-aware compatibility index independently from live-call status.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#validation-levels
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#release-policy
/// </remarks>
public sealed class CompatibilityEvidenceIndexBuilder
{
    public async Task<CompatibilityEvidenceIndex> BuildAndWriteAsync(
        string evidenceRoot,
        LiveEvidenceMatrix liveMatrix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(liveMatrix);
        var fullRoot = Path.GetFullPath(evidenceRoot);
        Directory.CreateDirectory(fullRoot);
        var contract = ContractCompatibilityVerifier.Verify();
        var rows = new List<CompatibilityEvidenceRow>(contract.Scenarios.Count);
        foreach (var scenario in contract.Scenarios)
        {
            var liveRow = liveMatrix.Rows.Single(row => string.Equals(
                row.ScenarioId,
                scenario.ScenarioId,
                StringComparison.Ordinal));
            var evidence = await ReadEvidenceAsync(
                    fullRoot,
                    liveRow.RunId,
                    cancellationToken)
                .ConfigureAwait(false);
            var client = evidence?.Subject.Client;
            rows.Add(new CompatibilityEvidenceRow(
                scenario.ScenarioId,
                ResolveValidationLevel(liveRow.Status, client),
                scenario.Verified,
                scenario.ProcessName,
                scenario.ProfileId,
                scenario.Surface,
                client?.ProcessName,
                client?.Version.Value,
                client?.ExecutableSha256,
                liveRow.Status,
                liveRow.ReasonCode,
                liveRow.RunId));
        }

        var verified = contract.AllScenariosVerified
            && contract.GenericBrowserFallbackVerified
            && contract.BrowserPlaybackNegativeVerified
            && rows.All(static row => row.ContractVerified);
        var index = new CompatibilityEvidenceIndex(
            "feat-011-compatibility-index-v1",
            DateTimeOffset.UtcNow,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            verified ? CompatibilityIndexStatus.Verified : CompatibilityIndexStatus.Failed,
            contract,
            BuildFingerprint.CreateDependencyManifest(),
            rows);
        var path = Path.Combine(fullRoot, "compatibility-index.json");
        var payload = JsonSerializer.SerializeToUtf8Bytes(index, EvidenceJson.Options);
        await WriteAtomicallyAsync(path, payload, cancellationToken).ConfigureAwait(false);
        var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(
            Path.Combine(fullRoot, "compatibility-index.sha256"),
            Encoding.UTF8.GetBytes($"{hash}  compatibility-index.json{Environment.NewLine}"),
            cancellationToken).ConfigureAwait(false);
        return index;
    }

    internal static ValidationEvidenceLevel ResolveValidationLevel(
        MatrixRowStatus liveStatus,
        ClientEvidence? client)
    {
        if (liveStatus == MatrixRowStatus.Passed)
        {
            return ValidationEvidenceLevel.LiveVerified;
        }

        var currentEvidence = liveStatus is
            MatrixRowStatus.Failed or
            MatrixRowStatus.Blocked or
            MatrixRowStatus.NotRun;
        return currentEvidence
               && !string.IsNullOrWhiteSpace(client?.ProcessName)
               && client.Version.Status == ObservationValueStatus.Observed
               && !string.IsNullOrWhiteSpace(client.ExecutableSha256)
            ? ValidationEvidenceLevel.EnvironmentObserved
            : ValidationEvidenceLevel.ContractVerified;
    }

    private static async Task<LiveEvidence?> ReadEvidenceAsync(
        string evidenceRoot,
        string? runId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        var path = Path.Combine(evidenceRoot, runId, "live-evidence.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<LiveEvidence>(
                    stream,
                    EvidenceJson.Options,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, payload.ToArray(), cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}

/// <summary>
/// Executes every declared profile/surface through the production matcher, assembler and scorer.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#acceptance
/// </remarks>
internal static class ContractCompatibilityVerifier
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-12T02:00:00Z");
    private static readonly ContractScenarioDefinition[] Definitions =
    [
        new("zoom.desktop.positive", "zoom", "desktop", "zoom.exe", "Zoom Meeting"),
        new("microsoft-teams.desktop.positive", "microsoft-teams", "desktop", "ms-teams.exe", "Microsoft Teams meeting"),
        new("microsoft-teams.browser.positive", "microsoft-teams", "browser", "zen.exe", "Meeting | Microsoft Teams"),
        new("google-meet.browser.positive", "google-meet", "browser", "zen.exe", "Planning — Google Meet"),
        new("yandex-telemost.desktop.positive", "yandex-telemost", "desktop", "yandextelemost.exe", "Яндекс Телемост"),
        new("yandex-telemost.browser.positive", "yandex-telemost", "browser", "zen.exe", "Яндекс Телемост"),
        new("kontur-talk.desktop.positive", "kontur-talk", "desktop", "tolk.exe", "Контур.Толк"),
        new("kontur-talk.browser.positive", "kontur-talk", "browser", "zen.exe", "Контур.Толк")
    ];

    public static ContractCompatibilityReport Verify()
    {
        var scenarios = Definitions.Select(VerifyScenario).ToArray();
        return new ContractCompatibilityReport(
            scenarios.All(static scenario => scenario.Verified),
            VerifyGenericBrowserFallback(),
            VerifyBrowserPlaybackNegative(),
            scenarios);
    }

    private static ContractScenarioResult VerifyScenario(ContractScenarioDefinition definition)
    {
        try
        {
            var result = Evaluate(
                definition.ProcessName,
                definition.WindowTitle,
                ["Turn off microphone", "Leave call"],
                definition.ProfileId);
            return new ContractScenarioResult(
                definition.ScenarioId,
                definition.ProfileId,
                definition.Surface,
                definition.ProcessName,
                result.PromptProfileId == definition.ProfileId,
                result.ConfidenceScore,
                result.PromptProfileId == definition.ProfileId ? null : "prompt_profile_mismatch");
        }
        catch (Exception exception)
        {
            return new ContractScenarioResult(
                definition.ScenarioId,
                definition.ProfileId,
                definition.Surface,
                definition.ProcessName,
                Verified: false,
                ConfidenceScore: null,
                exception.GetType().Name);
        }
    }

    private static bool VerifyGenericBrowserFallback()
    {
        var result = Evaluate(
            "zen.exe",
            "Project room — Zen Browser",
            ["Unmute", "Leave"],
            "generic-browser");
        return result.PromptProfileId == "generic-browser";
    }

    private static bool VerifyBrowserPlaybackNegative()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform("zen.exe");
        var windowEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, "YouTube — Zen Browser", true, ["Mute", "Full screen"])],
            Start);
        var assembler = new MeetingObservationAssembler(profiles);
        var engine = new MeetingDetectionEngine();
        var first = assembler.Assemble(audio, windowEvidence, Speech(), [], Start);
        var stable = assembler.Assemble(audio, windowEvidence, Speech(), [], Start.AddSeconds(3));
        var continued = assembler.Assemble(audio, windowEvidence, Speech(), [], Start.AddSeconds(6));
        _ = engine.EvaluateBatch(first, Start);
        var stableResult = engine.EvaluateBatch(stable, Start.AddSeconds(3));
        var continuedResult = engine.EvaluateBatch(continued, Start.AddSeconds(6));
        return stableResult.PromptCandidate is null && continuedResult.PromptCandidate is null;
    }

    private static ContractEvaluation Evaluate(
        string processName,
        string windowTitle,
        IReadOnlyList<string> controls,
        string expectedProfileId)
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(processName);
        var windows = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, windowTitle, true, controls)],
            Start);
        var profileWindows = windows
            .Where(window => string.Equals(
                window.ProfileId,
                expectedProfileId,
                StringComparison.Ordinal))
            .ToArray();
        var assembler = new MeetingObservationAssembler(profiles);
        var first = assembler.Assemble(audio, profileWindows, Speech(), [], Start);
        var stable = assembler.Assemble(audio, profileWindows, Speech(), [], Start.AddSeconds(3));
        var engine = new MeetingDetectionEngine();
        _ = engine.EvaluateBatch(first, Start);
        var prompt = engine.EvaluateBatch(stable, Start.AddSeconds(3)).PromptCandidate;
        if (prompt is null)
        {
            var askStable = assembler.Assemble(
                audio,
                profileWindows,
                Speech(),
                [],
                Start.AddSeconds(6));
            prompt = engine.EvaluateBatch(askStable, Start.AddSeconds(6)).PromptCandidate;
        }

        return new ContractEvaluation(prompt?.Frame.ProfileId, prompt?.Score.Score);
    }

    private static AudioPlatformSnapshot Platform(string processName) => new(
        Start,
        new AudioPlatformCapabilities(true, true, "contract-verifier"),
        [new AudioEndpointSnapshot("output", "Output", true, true)],
        [],
        [new ObservedProcessSnapshot(42, processName, new HashSet<int> { 42, 501 })],
        [
            new AudioSignalSnapshot(
                Start,
                processName,
                42,
                "AudioSessionStateActive",
                -24,
                "output",
                IsWatchedProcess: true,
                IsProcessTreeMatch: true)
        ]);

    private static MeetingSpeechActivitySnapshot Speech() => new(
        Start,
        new Dictionary<int, MeetingSpeechActivitySummary>
        {
            [42] = new(0.9, true, TimeSpan.FromSeconds(4), 0.7)
        },
        MeetingSpeechActivitySummary.Empty,
        new Dictionary<int, double>());

    private sealed record ContractScenarioDefinition(
        string ScenarioId,
        string ProfileId,
        string Surface,
        string ProcessName,
        string WindowTitle);

    private sealed record ContractEvaluation(string? PromptProfileId, int? ConfidenceScore);
}

public sealed record CompatibilityEvidenceIndex(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string Architecture,
    CompatibilityIndexStatus Status,
    ContractCompatibilityReport Contract,
    IReadOnlyDictionary<string, string> DependencySha256,
    IReadOnlyList<CompatibilityEvidenceRow> Rows);

public sealed record CompatibilityEvidenceRow(
    string ScenarioId,
    ValidationEvidenceLevel ValidationLevel,
    bool ContractVerified,
    string FixtureProcessName,
    string ProfileId,
    string Surface,
    string? ObservedClientProcessName,
    string? ObservedClientVersion,
    string? ObservedClientExecutableSha256,
    MatrixRowStatus LiveStatus,
    string? LiveReasonCode,
    string? LiveRunId);

public sealed record ContractCompatibilityReport(
    bool AllScenariosVerified,
    bool GenericBrowserFallbackVerified,
    bool BrowserPlaybackNegativeVerified,
    IReadOnlyList<ContractScenarioResult> Scenarios);

public sealed record ContractScenarioResult(
    string ScenarioId,
    string ProfileId,
    string Surface,
    string ProcessName,
    bool Verified,
    int? ConfidenceScore,
    string? FailureReason);

public enum CompatibilityIndexStatus
{
    Verified,
    Failed
}

public enum ValidationEvidenceLevel
{
    ContractVerified,
    EnvironmentObserved,
    LiveVerified
}

/// <summary>
/// Keeps staged live-call status diagnostic while compatibility owns FEAT-011 completion.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#release-policy
/// </remarks>
internal static class CompatibilityAggregateExitPolicy
{
    public static int GetExitCode(
        MatrixOutcomeStatus liveMatrixStatus,
        CompatibilityIndexStatus compatibilityStatus)
    {
        _ = liveMatrixStatus;
        return compatibilityStatus == CompatibilityIndexStatus.Verified ? 0 : 1;
    }
}

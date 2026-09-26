using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Aggregates the fixed FEAT-011 surface matrix and refuses a release pass when
/// evidence is missing, stale, mixed across builds, invalid, blocked, or not run.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#release-policy
/// </remarks>
public sealed class LiveEvidenceMatrixBuilder
{
    public static IReadOnlyList<string> RequiredScenarioIds { get; } =
    [
        "zoom.desktop.positive",
        "microsoft-teams.desktop.positive",
        "microsoft-teams.browser.positive",
        "google-meet.browser.positive",
        "yandex-telemost.desktop.positive",
        "yandex-telemost.browser.positive",
        "kontur-talk.desktop.positive",
        "kontur-talk.browser.positive"
    ];

    public static TimeSpan FreshnessWindow { get; } = TimeSpan.FromDays(14);

    public async Task<LiveEvidenceMatrix> BuildAndWriteAsync(
        string evidenceRoot,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(evidenceRoot);
        Directory.CreateDirectory(fullRoot);
        var loaded = new List<LoadedLiveEvidence>();
        foreach (var path in Directory.EnumerateFiles(
                     fullRoot,
                     "live-evidence.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var evidence = await JsonSerializer.DeserializeAsync<LiveEvidence>(
                        stream,
                        EvidenceJson.Options,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (evidence is not null)
                {
                    loaded.Add(new LoadedLiveEvidence(
                        evidence,
                        VerifyArtifactIntegrity(Path.GetDirectoryName(path)!, evidence)));
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        var matrix = Build(
            loaded,
            BuildFingerprint.CreateDependencyManifest(),
            DateTimeOffset.UtcNow);
        var matrixPath = Path.Combine(fullRoot, "live-matrix.json");
        var payload = JsonSerializer.SerializeToUtf8Bytes(matrix, EvidenceJson.Options);
        await WriteAtomicallyAsync(matrixPath, payload, cancellationToken).ConfigureAwait(false);
        var hash = await HashFileAsync(matrixPath, cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(
            Path.Combine(fullRoot, "live-matrix.sha256"),
            Encoding.UTF8.GetBytes($"{hash}  live-matrix.json{Environment.NewLine}"),
            cancellationToken).ConfigureAwait(false);
        return matrix;
    }

    internal static LiveEvidenceMatrix Build(
        IReadOnlyList<LoadedLiveEvidence> evidence,
        IReadOnlyDictionary<string, string> currentDependencies,
        DateTimeOffset nowUtc)
    {
        var rows = new List<LiveEvidenceMatrixRow>(RequiredScenarioIds.Count);
        foreach (var scenarioId in RequiredScenarioIds)
        {
            var latest = evidence
                .Where(item => string.Equals(
                    item.Evidence.ScenarioId,
                    scenarioId,
                    StringComparison.Ordinal))
                .OrderByDescending(static item => item.Evidence.GeneratedAtUtc)
                .FirstOrDefault();
            if (latest is null)
            {
                rows.Add(new LiveEvidenceMatrixRow(
                    scenarioId,
                    MatrixRowStatus.Missing,
                    RunId: null,
                    GeneratedAtUtc: null,
                    ReasonCode: "evidence_missing"));
                continue;
            }

            var validationErrors = LiveEvidenceValidator.Validate(latest.Evidence);
            var status = !latest.IntegrityValid
                    || validationErrors.Count > 0
                    || latest.Evidence.GeneratedAtUtc > nowUtc.AddMinutes(5)
                ? MatrixRowStatus.Invalid
                : !DependencyManifestsMatch(
                    latest.Evidence.Build.DependencySha256,
                    currentDependencies)
                    ? MatrixRowStatus.StaleBuild
                    : nowUtc - latest.Evidence.GeneratedAtUtc > FreshnessWindow
                        ? MatrixRowStatus.StaleEvidence
                        : MapOutcome(latest.Evidence.Outcome.Status);
            var reason = status switch
            {
                MatrixRowStatus.Invalid => "evidence_or_manifest_invalid",
                MatrixRowStatus.StaleBuild => "build_manifest_mismatch",
                MatrixRowStatus.StaleEvidence => "evidence_older_than_14_days",
                _ => latest.Evidence.Outcome.ReasonCode
            };
            rows.Add(new LiveEvidenceMatrixRow(
                scenarioId,
                status,
                latest.Evidence.RunId,
                latest.Evidence.GeneratedAtUtc,
                reason));
        }

        var passed = rows.All(static row => row.Status == MatrixRowStatus.Passed);
        return new LiveEvidenceMatrix(
            "feat-011-live-matrix-v1",
            nowUtc,
            FreshnessWindow.TotalDays,
            passed ? MatrixOutcomeStatus.Passed : MatrixOutcomeStatus.Incomplete,
            rows);
    }

    private static MatrixRowStatus MapOutcome(RunOutcomeStatus status) => status switch
    {
        RunOutcomeStatus.Passed => MatrixRowStatus.Passed,
        RunOutcomeStatus.Failed => MatrixRowStatus.Failed,
        RunOutcomeStatus.Blocked => MatrixRowStatus.Blocked,
        RunOutcomeStatus.NotRun => MatrixRowStatus.NotRun,
        _ => MatrixRowStatus.Invalid
    };

    private static bool DependencyManifestsMatch(
        IReadOnlyDictionary<string, string> evidence,
        IReadOnlyDictionary<string, string> current) =>
        evidence.Count == current.Count
        && evidence.All(pair =>
            current.TryGetValue(pair.Key, out var hash)
            && string.Equals(hash, pair.Value, StringComparison.Ordinal));

    private static bool VerifyArtifactIntegrity(
        string artifactDirectory,
        LiveEvidence evidence)
    {
        var manifestPath = Path.Combine(artifactDirectory, "sha256.txt");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "live-evidence.json",
            "host.log",
            "build-manifest.json",
            "feat-011-live-v1.schema.json"
        };
        foreach (var line in File.ReadLines(manifestPath))
        {
            if (line.Length < 67)
            {
                return false;
            }

            var expectedHash = line[..64];
            var fileName = line[64..].Trim();
            if (Path.GetFileName(fileName) != fileName || !required.Remove(fileName))
            {
                return false;
            }

            var path = Path.Combine(artifactDirectory, fileName);
            var actualHash = BuildFingerprint.TryHashFile(path);
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (required.Count != 0)
        {
            return false;
        }

        try
        {
            using var buildManifest = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(artifactDirectory, "build-manifest.json")));
            var files = buildManifest.RootElement
                .GetProperty("files")
                .Deserialize<Dictionary<string, string>>(EvidenceJson.Options);
            if (files is null || !DependencyManifestsMatch(files, evidence.Build.DependencySha256))
            {
                return false;
            }

            var schemaPath = Path.Combine(artifactDirectory, "feat-011-live-v1.schema.json");
            return evidence.Build.DependencySha256.TryGetValue(
                    "feat-011-live-v1.schema.json",
                    out var expectedSchemaHash)
                && string.Equals(
                    expectedSchemaHash,
                    BuildFingerprint.TryHashFile(schemaPath),
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or KeyNotFoundException)
        {
            return false;
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

public sealed record LiveEvidenceMatrix(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    double FreshnessWindowDays,
    MatrixOutcomeStatus Status,
    IReadOnlyList<LiveEvidenceMatrixRow> Rows);

public sealed record LiveEvidenceMatrixRow(
    string ScenarioId,
    MatrixRowStatus Status,
    string? RunId,
    DateTimeOffset? GeneratedAtUtc,
    string? ReasonCode);

public enum MatrixOutcomeStatus
{
    Passed,
    Incomplete
}

public enum MatrixRowStatus
{
    Passed,
    Failed,
    Blocked,
    NotRun,
    Missing,
    Invalid,
    StaleBuild,
    StaleEvidence
}

internal sealed record LoadedLiveEvidence(
    LiveEvidence Evidence,
    bool IntegrityValid);

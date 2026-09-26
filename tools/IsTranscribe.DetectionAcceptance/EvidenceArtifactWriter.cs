using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Writes an atomic, hash-addressed evidence bundle and substitutes a synthetic
/// log whenever the source log fails its privacy gate.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal static class EvidenceArtifactWriter
{
    public static async Task<string> WriteAsync(
        string outputRoot,
        LiveEvidence evidence,
        string? sourceLogPath,
        bool retainSourceLog,
        CancellationToken cancellationToken)
    {
        var validationErrors = LiveEvidenceValidator.Validate(evidence);
        if (validationErrors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validationErrors));
        }

        var artifactDirectory = Path.Combine(outputRoot, evidence.RunId);
        Directory.CreateDirectory(artifactDirectory);
        var targetLogPath = Path.Combine(artifactDirectory, "host.log");
        if (retainSourceLog && !string.IsNullOrWhiteSpace(sourceLogPath) && File.Exists(sourceLogPath))
        {
            File.Copy(sourceLogPath, targetLogPath, overwrite: true);
        }
        else
        {
            var statusLine = JsonSerializer.Serialize(
                new
                {
                    timestamp_utc = evidence.GeneratedAtUtc,
                    level = "Info",
                    event_code = "ACCEPTANCE_RUNNER_OUTCOME",
                    session_id = (string?)null,
                    job_name = "feat-011-live",
                    message = string.IsNullOrWhiteSpace(sourceLogPath)
                        ? "Acceptance scenario did not start the application runtime."
                        : "The runtime log was not retained because its privacy gate did not pass.",
                    metadata = new
                    {
                        outcome = evidence.Outcome.Status.ToString().ToLowerInvariant(),
                        reason_code = evidence.Outcome.ReasonCode
                    }
                },
                EvidenceJson.Options);
            await File.WriteAllTextAsync(
                targetLogPath,
                statusLine + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
        }

        var evidencePath = Path.Combine(artifactDirectory, "live-evidence.json");
        var payload = JsonSerializer.SerializeToUtf8Bytes(evidence, EvidenceJson.Options);
        await WriteAtomicallyAsync(evidencePath, payload, cancellationToken).ConfigureAwait(false);

        var buildManifestPath = Path.Combine(artifactDirectory, "build-manifest.json");
        var buildManifest = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schema_version = LiveEvidenceValidator.SchemaVersion,
                files = evidence.Build.DependencySha256
            },
            EvidenceJson.Options);
        await WriteAtomicallyAsync(buildManifestPath, buildManifest, cancellationToken).ConfigureAwait(false);

        var artifactPaths = new List<string> { evidencePath, targetLogPath, buildManifestPath };
        var sourceSchemaPath = Path.Combine(AppContext.BaseDirectory, "feat-011-live-v1.schema.json");
        if (File.Exists(sourceSchemaPath))
        {
            var targetSchemaPath = Path.Combine(artifactDirectory, "feat-011-live-v1.schema.json");
            File.Copy(sourceSchemaPath, targetSchemaPath, overwrite: true);
            artifactPaths.Add(targetSchemaPath);
        }

        var hashes = new StringBuilder();
        foreach (var path in artifactPaths.Order(StringComparer.Ordinal))
        {
            hashes.Append(await HashFileAsync(path, cancellationToken).ConfigureAwait(false));
            hashes.Append("  ");
            hashes.AppendLine(Path.GetFileName(path));
        }

        await WriteAtomicallyAsync(
            Path.Combine(artifactDirectory, "sha256.txt"),
            Encoding.UTF8.GetBytes(hashes.ToString()),
            cancellationToken).ConfigureAwait(false);
        return artifactDirectory;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteAtomicallyAsync(
        string targetPath,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, payload.ToArray(), cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
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

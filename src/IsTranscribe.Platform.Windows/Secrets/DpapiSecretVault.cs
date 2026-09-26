using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using IsTranscribe.Application.Platform;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Platform.Windows.Secrets;

/// <summary>
/// Key-addressable Windows vault stored as one DPAPI-protected JSON envelope.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.contract
/// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.operations
/// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.invariants
/// </remarks>
public sealed class DpapiSecretVault : ISecretVault
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(
        StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _secretsFilePath;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate;

    public DpapiSecretVault(LocalAppPaths paths, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _secretsFilePath = Path.GetFullPath(paths.SecretsFilePath);
        _gate = PathGates.GetOrAdd(_secretsFilePath, static _ => new SemaphoreSlim(1, 1));
    }

    public async ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = await LoadEnvelopeAsync(
                failOnCorruptPayload: false,
                cancellationToken).ConfigureAwait(false);
            if (envelope is null
                || !envelope.TryGetPropertyValue(normalizedKey, out var node)
                || node is not JsonValue value
                || !value.TryGetValue<string>(out var secret))
            {
                return null;
            }

            return secret;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeKey(key);
        ValidateValue(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fileExists = File.Exists(_secretsFilePath);
            if (!fileExists && value is null)
            {
                return;
            }

            var envelope = await LoadEnvelopeAsync(
                failOnCorruptPayload: true,
                cancellationToken).ConfigureAwait(false) ?? new JsonObject();
            if (value is null)
            {
                envelope.Remove(normalizedKey);
            }
            else
            {
                envelope[normalizedKey] = value;
            }

            await SaveEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<JsonObject?> LoadEnvelopeAsync(
        bool failOnCorruptPayload,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_secretsFilePath))
        {
            return new JsonObject();
        }

        byte[]? plaintext = null;
        try
        {
            var protectedPayload = await File.ReadAllBytesAsync(
                _secretsFilePath,
                cancellationToken).ConfigureAwait(false);
            plaintext = _protector.Unprotect(protectedPayload);
            return JsonNode.Parse(plaintext) as JsonObject
                ?? throw new JsonException("The secret vault envelope must be a JSON object.");
        }
        catch (Exception exception) when (IsCorruptPayloadException(exception))
        {
            if (failOnCorruptPayload)
            {
                throw new InvalidDataException(
                    "The existing secret vault payload is unreadable and was left unchanged.",
                    exception);
            }

            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async ValueTask SaveEnvelopeAsync(
        JsonObject envelope,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        byte[] protectedPayload;
        try
        {
            protectedPayload = _protector.Protect(plaintext);
            if (ReferenceEquals(plaintext, protectedPayload))
            {
                protectedPayload = protectedPayload.ToArray();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        await WriteAtomicallyAsync(
            _secretsFilePath,
            protectedPayload,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteAtomicallyAsync(
        string targetPath,
        byte[] protectedPayload,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The secret vault path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                protectedPayload,
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(targetPath))
            {
                File.Replace(
                    temporaryPath,
                    targetPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsCorruptPayloadException(Exception exception) =>
        exception is CryptographicException or JsonException or InvalidOperationException;

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A secret vault key is required.", nameof(key));
        }

        var normalized = key.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The secret vault key is invalid.", nameof(key));
        }

        return normalized;
    }

    private static void ValidateValue(string? value)
    {
        if (value is not null && (value.Length == 0 || value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException(
                "A secret vault value must be non-empty and contain no line breaks.",
                nameof(value));
        }
    }
}

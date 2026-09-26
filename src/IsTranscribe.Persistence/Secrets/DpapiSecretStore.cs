using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Secrets;

public sealed class DpapiSecretStore(LocalAppPaths paths, ISecretProtector protector) : ISecretStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly LocalAppPaths _paths = paths;
    private readonly ISecretProtector _protector = protector;

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.operations
    public async ValueTask<AppSecrets> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.SecretsFilePath))
        {
            return AppSecrets.Empty;
        }

        try
        {
            var protectedPayload = await File.ReadAllBytesAsync(_paths.SecretsFilePath, cancellationToken);
            var plaintext = _protector.Unprotect(protectedPayload);
            var envelope = JsonSerializer.Deserialize<SecretsEnvelope>(plaintext, SerializerOptions);
            return new AppSecrets(envelope?.FireworksApiKey);
        }
        catch (CryptographicException)
        {
            return AppSecrets.Empty;
        }
        catch (JsonException)
        {
            return AppSecrets.Empty;
        }
        catch (InvalidOperationException)
        {
            return AppSecrets.Empty;
        }
    }

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.operations
    public async ValueTask SaveAsync(AppSecrets secrets, CancellationToken cancellationToken)
    {
        var envelope = new SecretsEnvelope(secrets.FireworksApiKey);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        var protectedPayload = _protector.Protect(plaintext);
        await AtomicFileWriter.WriteAsync(_paths.SecretsFilePath, protectedPayload, cancellationToken);
    }

    private sealed record SecretsEnvelope([property: JsonPropertyName("fireworks_api_key")] string? FireworksApiKey);
}

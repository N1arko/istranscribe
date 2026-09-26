using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows;
using IsTranscribe.Platform.Windows.Secrets;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Synthetic Windows secret-vault contracts. No live or user credentials are used.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.contract
/// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.operations
/// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#secrets.invariants
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretVaultTests
{
    [Fact]
    public async Task ProviderEntriesRoundTripIndependentlyWithoutPlaintextInProtectedBlob()
    {
        const string groqValue = "synthetic-groq-vault-value";
        const string openRouterValue = "synthetic-openrouter-vault-value";
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var protector = new SyntheticSecretProtector();
            var vault = new DpapiSecretVault(paths, protector);

            await vault.WriteAsync(
                RemoteTranscriptionSecrets.Groq,
                groqValue,
                CancellationToken.None);
            await vault.WriteAsync(
                RemoteTranscriptionSecrets.OpenRouter,
                openRouterValue,
                CancellationToken.None);

            Assert.Equal(
                groqValue,
                await vault.ReadAsync(RemoteTranscriptionSecrets.Groq, CancellationToken.None));
            Assert.Equal(
                openRouterValue,
                await vault.ReadAsync(RemoteTranscriptionSecrets.OpenRouter, CancellationToken.None));

            var protectedBlob = await File.ReadAllBytesAsync(paths.SecretsFilePath);
            Assert.False(ContainsSequence(protectedBlob, Encoding.UTF8.GetBytes(groqValue)));
            Assert.False(ContainsSequence(protectedBlob, Encoding.UTF8.GetBytes(openRouterValue)));
            Assert.False(ContainsSequence(
                protectedBlob,
                Encoding.UTF8.GetBytes(RemoteTranscriptionSecrets.Groq)));
            Assert.False(ContainsSequence(
                protectedBlob,
                Encoding.UTF8.GetBytes(RemoteTranscriptionSecrets.OpenRouter)));

            using var envelope = ReadEnvelope(paths, protector);
            Assert.Equal(
                groqValue,
                envelope.RootElement.GetProperty(RemoteTranscriptionSecrets.Groq).GetString());
            Assert.Equal(
                openRouterValue,
                envelope.RootElement.GetProperty(RemoteTranscriptionSecrets.OpenRouter).GetString());
            Assert.DoesNotContain(
                Directory.EnumerateFiles(paths.ConfigDirectory),
                static path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WritesAndDeleteMergePreserveLegacyFireworksAndUnknownFields()
    {
        const string legacyFireworks = "synthetic-legacy-fireworks-value";
        const string openRouterValue = "synthetic-openrouter-value";
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var protector = new SyntheticSecretProtector();
            await WriteProtectedEnvelopeAsync(
                paths,
                protector,
                $$"""
                {
                  "fireworks_api_key": "{{legacyFireworks}}",
                  "unknown_scalar": "synthetic-unknown-value",
                  "unknown_object": { "version": 7, "enabled": true },
                  "unknown_array": [1, "two", false]
                }
                """);
            var vault = new DpapiSecretVault(paths, protector);

            await vault.WriteAsync(
                RemoteTranscriptionSecrets.Groq,
                "synthetic-groq-value",
                CancellationToken.None);
            await vault.WriteAsync(
                RemoteTranscriptionSecrets.OpenRouter,
                openRouterValue,
                CancellationToken.None);
            await vault.WriteAsync(
                RemoteTranscriptionSecrets.Groq,
                value: null,
                CancellationToken.None);

            Assert.Null(await vault.ReadAsync(
                RemoteTranscriptionSecrets.Groq,
                CancellationToken.None));
            Assert.Equal(
                openRouterValue,
                await vault.ReadAsync(
                    RemoteTranscriptionSecrets.OpenRouter,
                    CancellationToken.None));

            using var envelope = ReadEnvelope(paths, protector);
            var rootElement = envelope.RootElement;
            Assert.Equal(legacyFireworks, rootElement.GetProperty("fireworks_api_key").GetString());
            Assert.Equal("synthetic-unknown-value", rootElement.GetProperty("unknown_scalar").GetString());
            Assert.Equal(7, rootElement.GetProperty("unknown_object").GetProperty("version").GetInt32());
            Assert.True(rootElement.GetProperty("unknown_object").GetProperty("enabled").GetBoolean());
            Assert.Equal(3, rootElement.GetProperty("unknown_array").GetArrayLength());
            Assert.False(rootElement.TryGetProperty(RemoteTranscriptionSecrets.Groq, out _));
            Assert.Equal(
                openRouterValue,
                rootElement.GetProperty(RemoteTranscriptionSecrets.OpenRouter).GetString());

            var legacyStore = new DpapiSecretStore(paths, protector);
            var legacySecrets = await legacyStore.LoadAsync(CancellationToken.None);
            Assert.Equal(legacyFireworks, legacySecrets.FireworksApiKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentVaultInstancesPreserveEveryIndependentEntry()
    {
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var protector = new SyntheticSecretProtector();
            var vaults = Enumerable.Range(0, 8)
                .Select(_ => new DpapiSecretVault(paths, protector))
                .ToArray();
            var entries = Enumerable.Range(0, 30)
                .Select(index => new KeyValuePair<string, string>(
                    $"synthetic.concurrent.secret-{index:D2}",
                    $"synthetic-value-{index:D2}"))
                .Prepend(new KeyValuePair<string, string>(
                    RemoteTranscriptionSecrets.OpenRouter,
                    "synthetic-openrouter-concurrent"))
                .Prepend(new KeyValuePair<string, string>(
                    RemoteTranscriptionSecrets.Groq,
                    "synthetic-groq-concurrent"))
                .ToArray();

            await Task.WhenAll(entries.Select((entry, index) =>
                vaults[index % vaults.Length]
                    .WriteAsync(entry.Key, entry.Value, CancellationToken.None)
                    .AsTask()));

            var verifier = new DpapiSecretVault(paths, protector);
            foreach (var entry in entries)
            {
                Assert.Equal(
                    entry.Value,
                    await verifier.ReadAsync(entry.Key, CancellationToken.None));
            }

            using var envelope = ReadEnvelope(paths, protector);
            Assert.Equal(entries.Length, envelope.RootElement.EnumerateObject().Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedEnvelopeReadsSafelyAndCannotBeSilentlyOverwritten()
    {
        const string legacyValue = "synthetic-legacy-value-inside-malformed-envelope";
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var protector = new SyntheticSecretProtector();
            var malformedPlaintext = Encoding.UTF8.GetBytes(
                $"{{\"fireworks_api_key\":\"{legacyValue}\",\"unterminated\":");
            var originalBlob = protector.Protect(malformedPlaintext);
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllBytesAsync(paths.SecretsFilePath, originalBlob);
            var vault = new DpapiSecretVault(paths, protector);

            Assert.Null(await vault.ReadAsync(
                RemoteTranscriptionSecrets.Groq,
                CancellationToken.None));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                vault.WriteAsync(
                    RemoteTranscriptionSecrets.Groq,
                    "synthetic-replacement-value",
                    CancellationToken.None).AsTask());

            Assert.DoesNotContain(legacyValue, exception.Message, StringComparison.Ordinal);
            Assert.Equal(originalBlob, await File.ReadAllBytesAsync(paths.SecretsFilePath));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(paths.ConfigDirectory),
                static path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidProtectedBlobReadsSafelyAndWriteLeavesItUntouched()
    {
        byte[] invalidProtectedBlob = [0x01, 0x02, 0x03, 0x04];
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllBytesAsync(paths.SecretsFilePath, invalidProtectedBlob);
            var vault = new DpapiSecretVault(paths, new SyntheticSecretProtector());

            Assert.Null(await vault.ReadAsync(
                RemoteTranscriptionSecrets.OpenRouter,
                CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                vault.WriteAsync(
                    RemoteTranscriptionSecrets.OpenRouter,
                    "synthetic-replacement-value",
                    CancellationToken.None).AsTask());

            Assert.Equal(invalidProtectedBlob, await File.ReadAllBytesAsync(paths.SecretsFilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsAdapterCreatesVaultWithExistingDpapiProtector()
    {
        var paths = new LocalAppPaths("isTranscribe", CreateRoot());

        try
        {
            var adapter = new WindowsApplicationPlatformRuntimeAdapter();
            var vault = Assert.IsType<DpapiSecretVault>(adapter.CreateSecretVault(paths));
            var protectorField = typeof(DpapiSecretVault).GetField(
                "_protector",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The vault protector field was not found.");

            Assert.IsType<DpapiSecretProtector>(protectorField.GetValue(vault));
            Assert.False(File.Exists(paths.SecretsFilePath));
        }
        finally
        {
            Directory.Delete(paths.RootDirectory, recursive: true);
        }
    }

    private static async Task WriteProtectedEnvelopeAsync(
        LocalAppPaths paths,
        SyntheticSecretProtector protector,
        string json)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        var protectedPayload = protector.Protect(Encoding.UTF8.GetBytes(json));
        await File.WriteAllBytesAsync(paths.SecretsFilePath, protectedPayload);
    }

    private static JsonDocument ReadEnvelope(
        LocalAppPaths paths,
        SyntheticSecretProtector protector)
    {
        var protectedPayload = File.ReadAllBytes(paths.SecretsFilePath);
        return JsonDocument.Parse(protector.Unprotect(protectedPayload));
    }

    private static bool ContainsSequence(byte[] source, byte[] candidate)
    {
        if (candidate.Length == 0 || candidate.Length > source.Length)
        {
            return false;
        }

        for (var index = 0; index <= source.Length - candidate.Length; index++)
        {
            if (source.AsSpan(index, candidate.Length).SequenceEqual(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-windows-vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SyntheticSecretProtector : ISecretProtector
    {
        private static ReadOnlySpan<byte> Header => [0x53, 0x56, 0x4c, 0x54];

        public byte[] Protect(byte[] payload)
        {
            var protectedPayload = new byte[Header.Length + payload.Length];
            Header.CopyTo(protectedPayload);
            for (var index = 0; index < payload.Length; index++)
            {
                protectedPayload[Header.Length + index] = (byte)(payload[index] ^ 0xa5);
            }

            return protectedPayload;
        }

        public byte[] Unprotect(byte[] payload)
        {
            if (payload.Length < Header.Length
                || !payload.AsSpan(0, Header.Length).SequenceEqual(Header))
            {
                throw new CryptographicException("Synthetic protected payload is invalid.");
            }

            var plaintext = new byte[payload.Length - Header.Length];
            for (var index = 0; index < plaintext.Length; index++)
            {
                plaintext[index] = (byte)(payload[Header.Length + index] ^ 0xa5);
            }

            return plaintext;
        }
    }
}

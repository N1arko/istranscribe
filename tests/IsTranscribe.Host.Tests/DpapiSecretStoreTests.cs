using System.Security.Cryptography;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsFireworksApiKey()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var store = new DpapiSecretStore(
                new LocalAppPaths("isTranscribe", root),
                new TestSecretProtector());

            await store.SaveAsync(new AppSecrets("secret-key"), CancellationToken.None);
            var secrets = await store.LoadAsync(CancellationToken.None);

            Assert.Equal("secret-key", secrets.FireworksApiKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmpty_WhenPayloadCannotBeUnprotected()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllBytesAsync(paths.SecretsFilePath, [1, 2, 3]);

            var store = new DpapiSecretStore(paths, new ThrowingSecretProtector());
            var secrets = await store.LoadAsync(CancellationToken.None);

            Assert.Null(secrets.FireworksApiKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] payload) => ReverseCopy(payload);

        public byte[] Unprotect(byte[] payload) => ReverseCopy(payload);

        private static byte[] ReverseCopy(byte[] payload)
        {
            var copy = payload.ToArray();
            Array.Reverse(copy);
            return copy;
        }
    }

    private sealed class ThrowingSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] payload) => payload;

        public byte[] Unprotect(byte[] payload) => throw new CryptographicException("bad payload");
    }
}

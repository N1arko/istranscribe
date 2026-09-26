namespace IsTranscribe.Host.Secrets;

public sealed record AppSecrets(string? FireworksApiKey)
{
    public static AppSecrets Empty { get; } = new(FireworksApiKey: null);
}

public interface ISecretStore
{
    ValueTask<AppSecrets> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(AppSecrets secrets, CancellationToken cancellationToken);
}

public interface ISecretProtector
{
    byte[] Protect(byte[] payload);

    byte[] Unprotect(byte[] payload);
}

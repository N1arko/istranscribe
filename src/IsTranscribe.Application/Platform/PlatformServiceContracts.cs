namespace IsTranscribe.Application.Platform;

/// <summary>
/// Normalized availability state for every OS capability shown to product code.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// </remarks>
public enum PlatformCapabilityState
{
    Available,
    NotRequested,
    PermissionRequired,
    NeedsRestart,
    Unsupported,
    Failed
}

public sealed record PlatformCapability(
    string Id,
    PlatformCapabilityState State,
    string Explanation);

public interface IPlatformCapabilityService
{
    IReadOnlyList<PlatformCapability> GetCapabilities();
}

public interface IPlatformShell
{
    ValueTask OpenFileAsync(string path, CancellationToken cancellationToken);

    ValueTask OpenContainingFolderAsync(string path, CancellationToken cancellationToken);

    ValueTask OpenUriAsync(Uri uri, CancellationToken cancellationToken);
}

public readonly record struct PlatformPoint(int X, int Y);

public interface IActiveWorkAreaProvider
{
    PlatformPoint? TryGetForegroundWindowCenter();
}

public interface IPermissionService
{
    ValueTask<PlatformCapability> GetStatusAsync(string permission, CancellationToken cancellationToken);

    ValueTask<PlatformCapability> RequestAsync(string permission, CancellationToken cancellationToken);

    ValueTask OpenSystemSettingsAsync(string permission, CancellationToken cancellationToken);
}

public interface ISecretVault
{
    ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken);

    ValueTask WriteAsync(string key, string? value, CancellationToken cancellationToken);
}

public interface ISystemNotificationService
{
    ValueTask ShowAsync(string title, string message, CancellationToken cancellationToken);
}

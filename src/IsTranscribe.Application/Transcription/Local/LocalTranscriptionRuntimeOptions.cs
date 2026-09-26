using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Application.Transcription.Local;

/// <summary>
/// Exact app/package-owned identity used to freeze every local run.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public sealed record LocalWhisperRuntimeIdentity(
    string RuntimeVersion,
    string RuntimeCommit,
    string RuntimeSourceArchiveSha256,
    string NativeBundleManifestSha256,
    int BridgeAbiVersion,
    int WorkerProtocolVersion)
{
    public static LocalWhisperRuntimeIdentity PinnedV1 { get; } = new(
        RuntimeVersion: "whisper.cpp-v1.9.1",
        RuntimeCommit: "f049fff95a089aa9969deb009cdd4892b3e74916",
        RuntimeSourceArchiveSha256: "279af4ce60dbf397362868f3bacc75b56a4332ac2541cae155070093f6aaf0e3",
        NativeBundleManifestSha256: "fe37a0d085edaab7925c25f8e62514fe714dda4c66cfe3f2c48e2bea191d2feb",
        BridgeAbiVersion: 1,
        WorkerProtocolVersion: checked((int)WorkerProtocol.CurrentVersion));

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeVersion);
        RequireLowerHex(RuntimeCommit, 40, nameof(RuntimeCommit));
        RequireLowerHex(RuntimeSourceArchiveSha256, 64, nameof(RuntimeSourceArchiveSha256));
        RequireLowerHex(NativeBundleManifestSha256, 64, nameof(NativeBundleManifestSha256));
        if (BridgeAbiVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BridgeAbiVersion));
        }

        if (WorkerProtocolVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkerProtocolVersion));
        }

        if (this != PinnedV1)
        {
            throw new ArgumentException(
                "The local runtime identity must match the app-owned FEAT-016 runtime pin.");
        }
    }

    private static void RequireLowerHex(string value, int length, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != length
            || value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                $"The value must contain exactly {length} lowercase hexadecimal characters.",
                parameterName);
        }
    }
}

/// <summary>
/// Platform composition inputs for the isolated local engine. Application never guesses a
/// packaged worker path or native inventory identity.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#application-runtime
/// </remarks>
public sealed record LocalTranscriptionRuntimeOptions(
    string WorkerExecutablePath,
    LocalTranscriptionBackend RequestedBackend,
    int ThreadCount,
    ILocalTranscriptionResourcePolicy ResourcePolicy,
    string? ModelStoreRoot = null,
    HttpClient? ModelDownloadHttpClient = null,
    ILocalWorkerClientFactory? WorkerClientFactory = null)
{
    public LocalWhisperRuntimeIdentity RuntimeIdentity { get; init; } = LocalWhisperRuntimeIdentity.PinnedV1;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkerExecutablePath);
        if (!Path.IsPathFullyQualified(WorkerExecutablePath))
        {
            throw new ArgumentException("The local worker executable path must be absolute.", nameof(WorkerExecutablePath));
        }

        ArgumentNullException.ThrowIfNull(RuntimeIdentity);
        RuntimeIdentity.Validate();
        ArgumentNullException.ThrowIfNull(ResourcePolicy);
        if (!Enum.IsDefined(RequestedBackend))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestedBackend));
        }

        if (ThreadCount is <= 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(ThreadCount));
        }

        if (ModelStoreRoot is not null && !Path.IsPathFullyQualified(ModelStoreRoot))
        {
            throw new ArgumentException("The local model store path must be absolute.", nameof(ModelStoreRoot));
        }
    }
}

using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Creates the packaged Windows local-transcription contour from explicit installation and
/// persistent-storage roots.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.windows
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </remarks>
public static class WindowsLocalTranscriptionComposition
{
    public const string WorkerExecutableName = "IsTranscribe.Transcription.Worker.exe";
    internal const int MaximumWorkerThreadCount = 8;

    public static LocalTranscriptionRuntimeOptions Create(
        string applicationBaseDirectory,
        string modelStoreRoot) =>
        Create(applicationBaseDirectory, modelStoreRoot, Environment.ProcessorCount);

    internal static LocalTranscriptionRuntimeOptions Create(
        string applicationBaseDirectory,
        string modelStoreRoot,
        int logicalProcessorCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelStoreRoot);
        if (logicalProcessorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalProcessorCount));
        }

        var applicationBase = Path.GetFullPath(applicationBaseDirectory);
        var canonicalModelStore = Path.GetFullPath(modelStoreRoot);
        var options = new LocalTranscriptionRuntimeOptions(
            WorkerExecutablePath: Path.Combine(applicationBase, WorkerExecutableName),
            RequestedBackend: LocalTranscriptionBackend.Auto,
            ThreadCount: ResolveThreadCount(logicalProcessorCount),
            ResourcePolicy: new WindowsLocalTranscriptionResourcePolicy(canonicalModelStore),
            ModelStoreRoot: canonicalModelStore);
        options.Validate();
        return options;
    }

    internal static int ResolveThreadCount(int logicalProcessorCount)
    {
        if (logicalProcessorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalProcessorCount));
        }

        var halfRoundedUp = (logicalProcessorCount / 2) + (logicalProcessorCount % 2);
        return Math.Clamp(halfRoundedUp, 1, MaximumWorkerThreadCount);
    }
}

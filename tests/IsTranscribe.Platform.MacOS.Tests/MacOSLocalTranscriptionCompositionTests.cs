using IsTranscribe.Host.Persistence;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.macos
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public sealed class MacOSLocalTranscriptionCompositionTests
{
    [Fact]
    public void PackagedCompositionUsesSiblingWorkerCanonicalStoreAndBoundedAutoBackend()
    {
        var applicationBase = Path.Combine(Path.GetTempPath(), "istranscribe-package");
        var modelStore = Path.Combine(Path.GetTempPath(), "istranscribe-models");

        var options = MacOSLocalTranscriptionComposition.Create(
            applicationBase,
            modelStore,
            logicalProcessorCount: 64);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(applicationBase), "IsTranscribe.Transcription.Worker"),
            options.WorkerExecutablePath);
        Assert.Equal(Path.GetFullPath(modelStore), options.ModelStoreRoot);
        Assert.Equal(LocalTranscriptionBackend.Auto, options.RequestedBackend);
        Assert.Equal(MacOSLocalTranscriptionComposition.MaximumWorkerThreadCount, options.ThreadCount);
        Assert.IsType<MacOSLocalTranscriptionResourcePolicy>(options.ResourcePolicy);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(16, 8)]
    [InlineData(128, 8)]
    public void WorkerThreadsUseHalfTheLogicalProcessorsWithinThePackageBound(
        int logicalProcessorCount,
        int expected)
    {
        Assert.Equal(
            expected,
            MacOSLocalTranscriptionComposition.ResolveThreadCount(logicalProcessorCount));
    }

    [Fact]
    public void MacOSEntrypointActivatesCanonicalModelLayoutAndLocalRuntimeOptions()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "IsTranscribe.App.MacOS",
            "Program.cs"));

        Assert.Contains("WhisperModelStorageLayout.GetDefaultRootPath()", source, StringComparison.Ordinal);
        Assert.Contains("MacOSLocalTranscriptionComposition.Create(", source, StringComparison.Ordinal);
        Assert.Contains("localTranscriptionOptions: localTranscriptionOptions", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}

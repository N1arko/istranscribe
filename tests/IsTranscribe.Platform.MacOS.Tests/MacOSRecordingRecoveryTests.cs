using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
public sealed class MacOSRecordingRecoveryTests
{
    [Fact]
    public async Task RecoversCheckpointedWaveAfterProcessTermination()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-recovery-{Guid.NewGuid():N}");
        var paths = new LocalAppPaths("isTranscribe", root);
        await using var connection = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
        var repository = new MeetingSessionRepository(connection);
        var resolver = new ArtifactPathResolver(paths);
        var settings = ApplicationSettings.Default with
        {
            ReleaseV2 = ReleaseV2Settings.Default with
            {
                RecordingsFolder = Path.Combine(root, "recordings")
            }
        };
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var temp = resolver.GetTempSessionDirectoryPath(settings, id);
        var output = Path.Combine(temp, "output.wav");
        var staged = resolver.GetStagedPrimaryAudioFilePath(settings, id, createdAt, "Recovered");
        using (var writer = new MacOSWaveSourceWriter(output, 0))
        {
            writer.Promote();
            writer.Append(Tone(440));
        }

        await repository.UpsertAsync(
            MeetingSessionRecord.Create(id, createdAt, "manual", "deviceloopback", "output", null) with
            {
                SourceApp = "Recovered",
                TempSessionPath = temp,
                StagedPrimaryPath = staged,
                AudioOutputPath = output
            },
            CancellationToken.None);
        var native = new RecoveryNative();
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        var coordinator = new MacOSRecordingSessionCoordinator(
            platform,
            repository,
            resolver,
            () => settings,
            new BootstrapFileLogger(paths.HostLogFilePath));
        try
        {
            await coordinator.RecoverPendingAsync(CancellationToken.None);

            var recent = Assert.Single(repository.ListRecent(10, 0));
            Assert.Equal("ready", recent.RecordingStatus);
            Assert.NotNull(recent.PrimaryAudioPath);
            Assert.True(File.Exists(recent.PrimaryAudioPath));
            Assert.False(Directory.Exists(temp));
            Assert.Null(recent.TempSessionPath);
            Assert.Null(recent.AudioOutputPath);
            Assert.Null(recent.AudioMicPath);
            Assert.False(recent.SourceCleanupPending);
        }
        finally
        {
            await coordinator.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static MacOSPcmFrame Tone(double frequency)
    {
        var samples = new float[4_800];
        for (var frame = 0; frame < samples.Length; frame++)
        {
            samples[frame] = (float)(Math.Sin(2 * Math.PI * frequency * frame / 48_000) * 0.25);
        }

        return new MacOSPcmFrame(DateTimeOffset.UtcNow, 1, samples.Length, 1, 48_000, samples);
    }

    private sealed class RecoveryNative : IMacOSAudioNative
    {
        public double HostTicksPerSecond => 1_000_000_000;
        public MacOSMicrophonePermission MicrophonePermission => MacOSMicrophonePermission.Denied;
        public IReadOnlyList<MacOSNativeProcess> EnumerateProcesses() => [];
        public IReadOnlyList<MacOSNativeDevice> EnumerateDevices() => [];
        public IMacOSNativeCapture StartProcessCapture(int processId, Action<MacOSPcmFrame> onFrame) => throw new NotSupportedException();
        public IMacOSNativeCapture StartSystemOutputCapture(Action<MacOSPcmFrame> onFrame) => throw new NotSupportedException();
        public IMacOSNativeCapture StartMicrophoneCapture(string deviceId, Action<MacOSPcmFrame> onFrame) => throw new NotSupportedException();
    }
}

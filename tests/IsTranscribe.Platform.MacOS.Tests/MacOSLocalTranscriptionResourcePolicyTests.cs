using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// </summary>
public sealed class MacOSLocalTranscriptionResourcePolicyTests
{
    [Fact]
    public async Task AllowsMeasuredResourcesAndOneShotLowPowerOverride()
    {
        var probe = new TestProbe(
            AvailableMemoryBytes: 2_000,
            AvailableDiskBytes: 3_000,
            IsLowPowerMode: true);
        var policy = new MacOSLocalTranscriptionResourcePolicy(probe);

        var blocked = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);
        var allowed = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual) with { HasOneShotPowerOverride = true },
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.AttentionRequired, blocked.Disposition);
        Assert.Equal("low_power_override_required", blocked.StableCode);
        Assert.True(blocked.CanUseOneShotManualOverride);
        Assert.Equal(LocalTranscriptionResourceDisposition.Allowed, allowed.Disposition);
    }

    [Fact]
    public async Task DefersAutomaticWorkDuringLowPowerMode()
    {
        var policy = new MacOSLocalTranscriptionResourcePolicy(new TestProbe(2_000, 3_000, true));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Automatic),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.Deferred, assessment.Disposition);
        Assert.Equal("low_power", assessment.StableCode);
        Assert.False(assessment.CanUseOneShotManualOverride);
    }

    [Theory]
    [InlineData(999, 2_000, 3_000, "insufficient_memory")]
    [InlineData(8_000, 999, 3_000, "insufficient_memory")]
    [InlineData(8_000, 2_000, 999, "insufficient_disk")]
    public async Task BlocksInsufficientMeasuredResources(
        long installedMemory,
        long availableMemory,
        long availableDisk,
        string expectedCode)
    {
        var policy = new MacOSLocalTranscriptionResourcePolicy(
            new TestProbe(availableMemory, availableDisk, false, installedMemory));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.AttentionRequired, assessment.Disposition);
        Assert.Equal(expectedCode, assessment.StableCode);
    }

    [Fact]
    public async Task ClampsAvailableMemoryToInstalledMemoryBeforeThresholdAssessment()
    {
        var policy = new MacOSLocalTranscriptionResourcePolicy(
            new TestProbe(
                AvailableMemoryBytes: 9_000,
                AvailableDiskBytes: 3_000,
                IsLowPowerMode: false,
                InstalledMemoryBytes: 999));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.AttentionRequired, assessment.Disposition);
        Assert.Equal("insufficient_memory", assessment.StableCode);
        Assert.Equal(999, assessment.State.AvailableMemoryBytes);
        Assert.Equal(999, policy.CurrentState.AvailableMemoryBytes);
    }

    [Fact]
    public async Task AllowsResourcesExactlyAtRequiredThresholds()
    {
        var policy = new MacOSLocalTranscriptionResourcePolicy(
            new TestProbe(1_000, 1_000, false, 1_000));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.Allowed, assessment.Disposition);
    }

    [Fact]
    public void AvailablePageCalculationUsesNonOverlappingBuckets()
    {
        var available = SystemMacOSLocalTranscriptionResourceProbe.CalculateAvailableMemoryBytes(
            freePages: 3,
            inactivePages: 5,
            pageSize: 4_096);

        Assert.Equal(32_768UL, available);
    }

    [Fact]
    public async Task ProbeFailureIsFailClosed()
    {
        var policy = new MacOSLocalTranscriptionResourcePolicy(new FailingProbe());

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.AttentionRequired, assessment.Disposition);
        Assert.Equal("resource_probe_failed", assessment.StableCode);
        Assert.Equal("resource_probe_failed", policy.CurrentState.StableBlockCode);
    }

    [Fact]
    public void SystemProbeReturnsBoundedEvidenceOnMacOS()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return;
        }

        var probe = new SystemMacOSLocalTranscriptionResourceProbe(Path.GetTempPath());

        Assert.True(probe.GetInstalledMemoryBytes() > 0);
        Assert.True(probe.GetAvailableMemoryBytes() > 0);
        Assert.True(probe.GetAvailableDiskBytes() > 0);
        _ = probe.IsLowPowerModeEnabled();

        var state = new MacOSLocalTranscriptionResourcePolicy(probe).CurrentState;
        Assert.True(state.AvailableMemoryBytes <= state.InstalledMemoryBytes);
    }

    private static LocalTranscriptionResourceRequest Request(TranscriptionTriggerKind trigger) => new(
        JobId: "job",
        SessionId: "session",
        TriggerKind: trigger,
        RequiredMemoryBytes: 1_000,
        RequiredDiskBytes: 1_000,
        HasOneShotPowerOverride: false);

    private sealed record TestProbe(
        long AvailableMemoryBytes,
        long AvailableDiskBytes,
        bool IsLowPowerMode,
        long InstalledMemoryBytes = 8_000) : IMacOSLocalTranscriptionResourceProbe
    {
        public long GetInstalledMemoryBytes() => InstalledMemoryBytes;

        public long GetAvailableMemoryBytes() => AvailableMemoryBytes;

        public long GetAvailableDiskBytes() => AvailableDiskBytes;

        public bool IsLowPowerModeEnabled() => IsLowPowerMode;
    }

    private sealed class FailingProbe : IMacOSLocalTranscriptionResourceProbe
    {
        public long GetInstalledMemoryBytes() => throw new InvalidOperationException("synthetic");

        public long GetAvailableMemoryBytes() => throw new InvalidOperationException("synthetic");

        public long GetAvailableDiskBytes() => throw new InvalidOperationException("synthetic");

        public bool IsLowPowerModeEnabled() => throw new InvalidOperationException("synthetic");
    }
}

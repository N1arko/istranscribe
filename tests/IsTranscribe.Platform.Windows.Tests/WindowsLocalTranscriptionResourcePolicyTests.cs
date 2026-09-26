using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// </summary>
public sealed class WindowsLocalTranscriptionResourcePolicyTests
{
    [Fact]
    public async Task AllowsMeasuredResourcesAndPublishesPhysicalMemoryEvidence()
    {
        var policy = new WindowsLocalTranscriptionResourcePolicy(
            new TestProbe(TotalMemory: 8_000, AvailableMemory: 2_000, AvailableDisk: 3_000));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.Allowed, assessment.Disposition);
        Assert.Equal(8_000, assessment.State.InstalledMemoryBytes);
        Assert.Equal(2_000, assessment.State.AvailableMemoryBytes);
        Assert.Equal(3_000, assessment.State.AvailableDiskBytes);
    }

    [Fact]
    public async Task ManualEnergySaverOverrideAppliesToOneRequest()
    {
        var policy = new WindowsLocalTranscriptionResourcePolicy(
            new TestProbe(8_000, 2_000, 3_000, EnergySaverEnabled: true));

        var beforeOverride = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);
        var overridden = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual) with { HasOneShotPowerOverride = true },
            CancellationToken.None);
        var afterOverride = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(
            LocalTranscriptionResourceDisposition.AttentionRequired,
            beforeOverride.Disposition);
        Assert.Equal("low_power_override_required", beforeOverride.StableCode);
        Assert.True(beforeOverride.CanUseOneShotManualOverride);
        Assert.Equal(LocalTranscriptionResourceDisposition.Allowed, overridden.Disposition);
        Assert.Equal(
            LocalTranscriptionResourceDisposition.AttentionRequired,
            afterOverride.Disposition);
        Assert.Equal("low_power_override_required", afterOverride.StableCode);
    }

    [Fact]
    public async Task AutomaticWorkAlwaysDefersDuringEnergySaver()
    {
        var policy = new WindowsLocalTranscriptionResourcePolicy(
            new TestProbe(8_000, 2_000, 3_000, EnergySaverEnabled: true));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Automatic) with { HasOneShotPowerOverride = true },
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.Deferred, assessment.Disposition);
        Assert.Equal("energy_saver", assessment.StableCode);
        Assert.False(assessment.CanUseOneShotManualOverride);
    }

    [Theory]
    [InlineData(999, 999, 3_000, "insufficient_memory", "Installed memory")]
    [InlineData(8_000, 999, 3_000, "insufficient_memory", "Available memory")]
    [InlineData(8_000, 2_000, 999, "insufficient_disk", "Available disk")]
    public async Task BlocksInsufficientPhysicalResources(
        long totalMemory,
        long availableMemory,
        long availableDisk,
        string expectedCode,
        string expectedMessagePrefix)
    {
        var policy = new WindowsLocalTranscriptionResourcePolicy(
            new TestProbe(totalMemory, availableMemory, availableDisk));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.AttentionRequired, assessment.Disposition);
        Assert.Equal(expectedCode, assessment.StableCode);
        Assert.StartsWith(expectedMessagePrefix, assessment.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 3_000)]
    [InlineData(8_000, -1, 3_000)]
    [InlineData(8_000, 8_001, 3_000)]
    [InlineData(8_000, 2_000, -1)]
    public async Task InvalidMeasurementsFailClosed(
        long totalMemory,
        long availableMemory,
        long availableDisk)
    {
        var policy = new WindowsLocalTranscriptionResourcePolicy(
            new TestProbe(totalMemory, availableMemory, availableDisk));

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.AttentionRequired, assessment.Disposition);
        Assert.Equal("resource_probe_failed", assessment.StableCode);
        Assert.Equal("resource_probe_failed", assessment.State.StableBlockCode);
    }

    [Fact]
    public async Task EnergySaverProbeFailureIsFailClosed()
    {
        var policy = new WindowsLocalTranscriptionResourcePolicy(new FailingEnergySaverProbe());

        var assessment = await policy.AssessAsync(
            Request(TranscriptionTriggerKind.Manual),
            CancellationToken.None);

        Assert.Equal(LocalTranscriptionResourceDisposition.AttentionRequired, assessment.Disposition);
        Assert.Equal("resource_probe_failed", assessment.StableCode);
        Assert.Null(assessment.State.InstalledMemoryBytes);
        Assert.Equal("resource_probe_failed", policy.CurrentState.StableBlockCode);
    }

    [Fact]
    public async Task CancellationStopsAssessmentBeforeProbe()
    {
        var probe = new CountingProbe();
        var policy = new WindowsLocalTranscriptionResourcePolicy(probe);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await policy.AssessAsync(
                Request(TranscriptionTriggerKind.Manual),
                cancellation.Token));

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void SystemProbeReturnsPhysicalEvidenceOnWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return;
        }

        var probe = new SystemWindowsLocalTranscriptionResourceProbe(Path.GetTempPath());
        var memory = probe.GetPhysicalMemory();

        Assert.True(memory.TotalBytes > 0);
        Assert.InRange(memory.AvailableBytes, 0, memory.TotalBytes);
        Assert.True(probe.GetAvailableDiskBytes() >= 0);
        _ = probe.IsEnergySaverEnabled();
    }

    private static LocalTranscriptionResourceRequest Request(TranscriptionTriggerKind trigger) =>
        new(
            JobId: "job",
            SessionId: "session",
            TriggerKind: trigger,
            RequiredMemoryBytes: 1_000,
            RequiredDiskBytes: 1_000,
            HasOneShotPowerOverride: false);

    private sealed record TestProbe(
        long TotalMemory,
        long AvailableMemory,
        long AvailableDisk,
        bool EnergySaverEnabled = false) : IWindowsLocalTranscriptionResourceProbe
    {
        public WindowsPhysicalMemorySnapshot GetPhysicalMemory() =>
            new(TotalMemory, AvailableMemory);

        public long GetAvailableDiskBytes() => AvailableDisk;

        public bool IsEnergySaverEnabled() => EnergySaverEnabled;
    }

    private sealed class FailingEnergySaverProbe : IWindowsLocalTranscriptionResourceProbe
    {
        public WindowsPhysicalMemorySnapshot GetPhysicalMemory() => new(8_000, 2_000);

        public long GetAvailableDiskBytes() => 3_000;

        public bool IsEnergySaverEnabled() =>
            throw new InvalidOperationException("synthetic");
    }

    private sealed class CountingProbe : IWindowsLocalTranscriptionResourceProbe
    {
        public int CallCount { get; private set; }

        public WindowsPhysicalMemorySnapshot GetPhysicalMemory()
        {
            CallCount++;
            return new WindowsPhysicalMemorySnapshot(8_000, 2_000);
        }

        public long GetAvailableDiskBytes()
        {
            CallCount++;
            return 3_000;
        }

        public bool IsEnergySaverEnabled()
        {
            CallCount++;
            return false;
        }
    }
}

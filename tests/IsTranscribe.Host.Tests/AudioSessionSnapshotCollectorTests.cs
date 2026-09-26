using System.Runtime.Versioning;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Host.Audio.Sessions;
using NAudio.CoreAudioApi.Interfaces;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#zen
/// </summary>
public sealed class AudioSessionSnapshotCollectorTests
{
    [Fact]
    public void CollectMatchesSessionsAgainstTrackedProcessTree()
    {
        var observedAt = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        ObservedProcessSnapshot[] observedProcesses =
        [
            new ObservedProcessSnapshot(1200, "chrome.exe", new HashSet<int> { 1200, 1300 })
        ];

        SessionProbe[] probes =
        [
            new SessionProbe(1300, AudioSessionState.AudioSessionStateActive, 0.5f, "render-1")
        ];

        var result = AudioSessionSnapshotCollector.Collect(observedProcesses, probes, observedAt);

        var snapshot = Assert.Single(result);
        Assert.Equal(observedAt, snapshot.ObservedAtUtc);
        Assert.Equal("chrome.exe", snapshot.RootProcessName);
        Assert.Equal(1200, snapshot.RootProcessId);
        Assert.Equal("AudioSessionStateActive", snapshot.AudioSessionState);
        Assert.Equal("render-1", snapshot.RenderDeviceId);
        Assert.True(snapshot.IsWhitelisted);
        Assert.True(snapshot.IsProcessTreeMatch);
        Assert.True(snapshot.SignalLevelDbfs < 0d);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CollectAttributesNestedWatchedApplicationToItsOwnDisjointRoot()
    {
        var observedAt = new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero);
        ProcessTreeSnapshotBuilder.ProcessSnapshotEntry[] entries =
        [
            new(100, 1, "zen.exe"),
            new(200, 100, "zoom.exe"),
            new(201, 200, "zoom.exe")
        ];
        var processes = ProcessTreeSnapshotBuilder.BuildSnapshot(
            entries,
            new HashSet<string>(["zen.exe", "zoom.exe"], StringComparer.OrdinalIgnoreCase),
            observedAt);

        var result = AudioSessionSnapshotCollector.Collect(
            processes.Processes,
            [new SessionProbe(201, AudioSessionState.AudioSessionStateActive, 0.5f, "render-1")],
            observedAt);

        var snapshot = Assert.Single(result);
        Assert.Equal("zoom.exe", snapshot.RootProcessName);
        Assert.Equal(200, snapshot.RootProcessId);
        Assert.True(snapshot.IsProcessTreeMatch);
    }

    [Fact]
    public void CollectIncludesUnknownProcessSnapshotsWhenNoWhitelistMatchExists()
    {
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        var observedAt = new DateTimeOffset(2026, 4, 5, 12, 5, 0, TimeSpan.Zero);

        var result = AudioSessionSnapshotCollector.Collect(
            Array.Empty<ObservedProcessSnapshot>(),
            [new SessionProbe(currentProcess.Id, AudioSessionState.AudioSessionStateActive, 0.25f, "render-2")],
            observedAt);

        var snapshot = Assert.Single(result);
        Assert.Equal(observedAt, snapshot.ObservedAtUtc);
        Assert.Equal($"{currentProcess.ProcessName}.exe", snapshot.RootProcessName);
        Assert.Equal(currentProcess.Id, snapshot.RootProcessId);
        Assert.False(snapshot.IsWhitelisted);
        Assert.False(snapshot.IsProcessTreeMatch);
        Assert.Equal("render-2", snapshot.RenderDeviceId);
    }

    [Fact]
    public void ConvertPeakToDbfsReturnsFloorForSilence()
    {
        Assert.Equal(-100d, AudioSessionSnapshotCollector.ConvertPeakToDbfs(0f));
    }
}

using System.Runtime.Versioning;
using System.Diagnostics;
using IsTranscribe.Host.Audio.Processes;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#zen
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessTreeSnapshotBuilderTests
{
    [Fact]
    public void BuildTreeIncludesRootAndAllDescendants()
    {
        var childrenByParent = new Dictionary<int, int[]>
        {
            [100] = [101, 102],
            [101] = [103],
            [102] = [],
            [103] = [104]
        };

        var tree = ProcessTreeSnapshotBuilder.BuildTree(100, childrenByParent);

        Assert.Equal([100, 101, 102, 103, 104], tree.OrderBy(value => value).ToArray());
    }

    [Fact]
    public void BuildTreeStopsOnCycles()
    {
        var childrenByParent = new Dictionary<int, int[]>
        {
            [200] = [201],
            [201] = [202],
            [202] = [200]
        };

        var tree = ProcessTreeSnapshotBuilder.BuildTree(200, childrenByParent);

        Assert.Equal([200, 201, 202], tree.OrderBy(value => value).ToArray());
    }

    [Fact]
    public void BuildSnapshotCollapsesFirefoxStyleSameNameDescendantsIntoOneRoot()
    {
        var observedAt = new DateTimeOffset(2026, 4, 8, 20, 0, 0, TimeSpan.Zero);
        ProcessTreeSnapshotBuilder.ProcessSnapshotEntry[] entries =
        [
            new(1872, 14892, "zen.exe"),
            new(24208, 1872, "zen.exe"),
            new(20668, 1872, "zen.exe"),
            new(5000, 1, "notepad.exe")
        ];

        var snapshot = ProcessTreeSnapshotBuilder.BuildSnapshot(
            entries,
            new HashSet<string>(["zen.exe"], StringComparer.OrdinalIgnoreCase),
            observedAt);

        Assert.Equal(observedAt, snapshot.ObservedAtUtc);
        var zen = Assert.Single(snapshot.Processes);
        Assert.Equal("zen.exe", zen.RootProcessName);
        Assert.Equal(1872, zen.RootProcessId);
        Assert.Equal([1872, 20668, 24208], zen.ProcessTreeIds.Order().ToArray());
    }

    [Fact]
    public void BuildSnapshotKeepsIndependentSameNameRootsSeparate()
    {
        var observedAt = new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero);
        ProcessTreeSnapshotBuilder.ProcessSnapshotEntry[] entries =
        [
            new(100, 1, "zen.exe"),
            new(101, 100, "zen.exe"),
            new(200, 1, "zen.exe"),
            new(201, 200, "zen.exe")
        ];

        var snapshot = ProcessTreeSnapshotBuilder.BuildSnapshot(
            entries,
            new HashSet<string>(["zen.exe"], StringComparer.OrdinalIgnoreCase),
            observedAt);

        Assert.Equal([100, 200], snapshot.Processes.Select(static process => process.RootProcessId).ToArray());
        Assert.Equal([100, 101], snapshot.Processes[0].ProcessTreeIds.Order().ToArray());
        Assert.Equal([200, 201], snapshot.Processes[1].ProcessTreeIds.Order().ToArray());
    }

    [Fact]
    public void BuildSnapshotDoesNotCollapseDifferentWatchedExecutables()
    {
        var observedAt = new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero);
        ProcessTreeSnapshotBuilder.ProcessSnapshotEntry[] entries =
        [
            new(100, 1, "zen.exe"),
            new(200, 100, "zoom.exe")
        ];

        var snapshot = ProcessTreeSnapshotBuilder.BuildSnapshot(
            entries,
            new HashSet<string>(["zen.exe", "zoom.exe"], StringComparer.OrdinalIgnoreCase),
            observedAt);

        Assert.Equal([100, 200], snapshot.Processes.Select(static process => process.RootProcessId).ToArray());
        Assert.Equal([100], snapshot.Processes[0].ProcessTreeIds.Order().ToArray());
        Assert.Equal([200], snapshot.Processes[1].ProcessTreeIds.Order().ToArray());
    }

    [Fact]
    public void BuildSnapshotFindsCurrentProcessViaToolhelpEnumeration()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var watchedProcessName = $"{currentProcess.ProcessName}.exe";

        var snapshot = ProcessTreeSnapshotBuilder.BuildSnapshot([watchedProcessName], DateTimeOffset.UtcNow);

        Assert.Contains(snapshot.Processes, process => process.ProcessTreeIds.Contains(currentProcess.Id));
    }
}

using Avalonia;
using IsTranscribe.Desktop.Services;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#placement
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#verification
/// </remarks>
public sealed class RecordingOverlayPlacementTests
{
    [Fact]
    public void DefaultPositionUsesRightEdgeAndVerticalCenterOnNegativeOriginMonitor()
    {
        var workingArea = new PixelRect(-1920, 0, 1920, 1040);

        var position = RecordingOverlayPlacementPolicy.DefaultAtRightEdge(
            workingArea,
            new PixelSize(228, 48),
            margin: 8);

        Assert.Equal(new PixelPoint(-236, 496), position);
    }

    [Theory]
    [InlineData(-1000, 350, 18, 326)]
    [InlineData(5000, 350, 774, 326)]
    [InlineData(500, -1000, 386, 28)]
    [InlineData(500, 5000, 386, 664)]
    public void PositionAroundAnchorClampsEveryWorkingAreaEdge(
        double centerX,
        double centerY,
        int expectedX,
        int expectedY)
    {
        var position = RecordingOverlayPlacementPolicy.PositionAroundAnchor(
            new RecordingOverlayAnchor(centerX, centerY),
            new PixelSize(228, 48),
            new PixelRect(10, 20, 1000, 700),
            margin: 8);

        Assert.Equal(new PixelPoint(expectedX, expectedY), position);
    }

    [Fact]
    public void ExpandedAndCollapsedSizesPreserveTheSameCenterAnchor()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1040);
        var anchor = new RecordingOverlayAnchor(960, 520);

        var expandedPosition = RecordingOverlayPlacementPolicy.PositionAroundAnchor(
            anchor,
            new PixelSize(228, 48),
            workingArea,
            margin: 8);
        var collapsedPosition = RecordingOverlayPlacementPolicy.PositionAroundAnchor(
            anchor,
            new PixelSize(44, 48),
            workingArea,
            margin: 8);

        Assert.Equal(
            anchor,
            RecordingOverlayPlacementPolicy.AnchorFromPosition(
                expandedPosition,
                new PixelSize(228, 48)));
        Assert.Equal(
            anchor,
            RecordingOverlayPlacementPolicy.AnchorFromPosition(
                collapsedPosition,
                new PixelSize(44, 48)));
    }

    [Fact]
    public void DisconnectedMonitorAnchorSelectsNearestAvailableWorkingArea()
    {
        var primary = new PixelRect(0, 0, 1920, 1040);
        var secondary = new PixelRect(1920, 0, 1920, 1040);

        var selected = RecordingOverlayPlacementPolicy.SelectWorkingArea(
            new RecordingOverlayAnchor(-1200, 500),
            [primary, secondary]);

        Assert.Equal(primary, selected);
    }

    [Fact]
    public async Task PlacementStoreToleratesMissingAndCorruptDataAndRoundTripsLatestAnchor()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "recording-overlay-placement.json");
        var store = new LocalRecordingOverlayPlacementStore(path);

        Assert.Null(await store.LoadAsync(CancellationToken.None));

        await File.WriteAllTextAsync(path, "{not-json", CancellationToken.None);
        Assert.Null(await store.LoadAsync(CancellationToken.None));

        await File.WriteAllTextAsync(
            path,
            "{\"centerX\":1e300,\"centerY\":0}",
            CancellationToken.None);
        Assert.Null(await store.LoadAsync(CancellationToken.None));

        var initial = new RecordingOverlayAnchor(-720, 410);
        var updated = new RecordingOverlayAnchor(1260, 690);
        await store.SaveAsync(initial, CancellationToken.None);
        Assert.Equal(initial, await store.LoadAsync(CancellationToken.None));

        await store.SaveAsync(updated, CancellationToken.None);
        Assert.Equal(updated, await store.LoadAsync(CancellationToken.None));
        Assert.False(File.Exists(path + ".tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "IsTranscribe.Desktop.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

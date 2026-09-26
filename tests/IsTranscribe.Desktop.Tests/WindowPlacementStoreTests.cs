using IsTranscribe.Desktop.Services;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.geometry
/// </remarks>
public sealed class WindowPlacementStoreTests
{
    [Fact]
    public async Task Missing_or_corrupt_placement_falls_back_without_startup_failure()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "placement.json");
        var store = new LocalWindowPlacementStore(path);

        Assert.Null(await store.LoadAsync(CancellationToken.None));

        await File.WriteAllTextAsync(path, "{not-json", CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Placement_round_trips_and_newer_geometry_replaces_previous_value_atomically()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "placement.json");
        var store = new LocalWindowPlacementStore(path);
        var initial = new WindowPlacement(120, 80, 460, 620);
        var updated = new WindowPlacement(-240, 140, 520, 700);

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

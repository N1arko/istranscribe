using System.Text.Json;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Persists the small amount of geometry needed to restore the compact window.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.geometry
/// </remarks>
public interface IWindowPlacementStore
{
    ValueTask<WindowPlacement?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(WindowPlacement placement, CancellationToken cancellationToken);
}

public sealed record WindowPlacement(int X, int Y, double Width, double Height);

/// <summary>
/// Local JSON placement store. The view validates the coordinates against current screens.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.geometry
/// </remarks>
public sealed class LocalWindowPlacementStore : IWindowPlacementStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public LocalWindowPlacementStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "isTranscribe",
            "window-placement.json");
    }

    public async ValueTask<WindowPlacement?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<WindowPlacement>(
                stream,
                SerializerOptions,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async ValueTask SaveAsync(WindowPlacement placement, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The window placement directory is unavailable.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, placement, SerializerOptions, cancellationToken);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }
}

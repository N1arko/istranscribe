using System.Text.Json;
using Avalonia;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Stable physical-screen anchor for the floating recording controls.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#placement
/// </remarks>
public readonly record struct RecordingOverlayAnchor(double CenterX, double CenterY);

/// <summary>
/// Persists the user-selected overlay anchor independently from the main window.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#placement
/// </remarks>
public interface IRecordingOverlayPlacementStore
{
    ValueTask<RecordingOverlayAnchor?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(RecordingOverlayAnchor anchor, CancellationToken cancellationToken);
}

/// <summary>
/// Atomic local JSON store for the floating recording controls.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#placement
/// </remarks>
public sealed class LocalRecordingOverlayPlacementStore : IRecordingOverlayPlacementStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _path;

    public LocalRecordingOverlayPlacementStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "isTranscribe",
            "recording-overlay-placement.json");
    }

    public async ValueTask<RecordingOverlayAnchor?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            await using var stream = File.OpenRead(_path);
            var anchor = await JsonSerializer.DeserializeAsync<RecordingOverlayAnchor>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return anchor is { } value && IsRestorable(value)
                ? value
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async ValueTask SaveAsync(RecordingOverlayAnchor anchor, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The recording overlay placement directory is unavailable.");
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
                await JsonSerializer.SerializeAsync(
                        stream,
                        anchor,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static bool IsRestorable(RecordingOverlayAnchor anchor) =>
        double.IsFinite(anchor.CenterX)
        && double.IsFinite(anchor.CenterY)
        && anchor.CenterX >= int.MinValue / 2d
        && anchor.CenterX <= int.MaxValue / 2d
        && anchor.CenterY >= int.MinValue / 2d
        && anchor.CenterY <= int.MaxValue / 2d;
}

/// <summary>
/// Pure placement rules shared by native positioning and deterministic tests.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#placement
/// </remarks>
internal static class RecordingOverlayPlacementPolicy
{
    public static PixelPoint DefaultAtRightEdge(
        PixelRect workingArea,
        PixelSize windowSize,
        int margin)
    {
        var x = ClampCoordinate(
            workingArea.Right - windowSize.Width - margin,
            workingArea.X,
            workingArea.Width,
            windowSize.Width,
            margin);
        var y = ClampCoordinate(
            workingArea.Y + ((workingArea.Height - windowSize.Height) / 2),
            workingArea.Y,
            workingArea.Height,
            windowSize.Height,
            margin);
        return new PixelPoint(x, y);
    }

    public static RecordingOverlayAnchor AnchorFromPosition(PixelPoint position, PixelSize windowSize) =>
        new(
            position.X + (windowSize.Width / 2d),
            position.Y + (windowSize.Height / 2d));

    public static PixelPoint PositionAroundAnchor(
        RecordingOverlayAnchor anchor,
        PixelSize windowSize,
        PixelRect workingArea,
        int margin)
    {
        var desiredX = checked((int)Math.Round(
            anchor.CenterX - (windowSize.Width / 2d),
            MidpointRounding.AwayFromZero));
        var desiredY = checked((int)Math.Round(
            anchor.CenterY - (windowSize.Height / 2d),
            MidpointRounding.AwayFromZero));
        return new PixelPoint(
            ClampCoordinate(
                desiredX,
                workingArea.X,
                workingArea.Width,
                windowSize.Width,
                margin),
            ClampCoordinate(
                desiredY,
                workingArea.Y,
                workingArea.Height,
                windowSize.Height,
                margin));
    }

    public static PixelRect SelectWorkingArea(
        RecordingOverlayAnchor anchor,
        IReadOnlyList<PixelRect> workingAreas)
    {
        ArgumentNullException.ThrowIfNull(workingAreas);
        if (workingAreas.Count == 0)
        {
            throw new ArgumentException("At least one working area is required.", nameof(workingAreas));
        }

        foreach (var workingArea in workingAreas)
        {
            if (Contains(workingArea, anchor))
            {
                return workingArea;
            }
        }

        return workingAreas
            .OrderBy(workingArea => DistanceSquared(workingArea, anchor))
            .First();
    }

    private static int ClampCoordinate(
        int desired,
        int areaStart,
        int areaLength,
        int windowLength,
        int margin)
    {
        var minimum = areaStart + margin;
        var maximum = areaStart + areaLength - windowLength - margin;
        if (maximum < minimum)
        {
            return areaStart + ((areaLength - windowLength) / 2);
        }

        return Math.Clamp(desired, minimum, maximum);
    }

    private static bool Contains(PixelRect area, RecordingOverlayAnchor anchor) =>
        anchor.CenterX >= area.X
        && anchor.CenterX < area.Right
        && anchor.CenterY >= area.Y
        && anchor.CenterY < area.Bottom;

    private static double DistanceSquared(PixelRect area, RecordingOverlayAnchor anchor)
    {
        var deltaX = anchor.CenterX < area.X
            ? area.X - anchor.CenterX
            : anchor.CenterX > area.Right
                ? anchor.CenterX - area.Right
                : 0;
        var deltaY = anchor.CenterY < area.Y
            ? area.Y - anchor.CenterY
            : anchor.CenterY > area.Bottom
                ? anchor.CenterY - area.Bottom
                : 0;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }
}

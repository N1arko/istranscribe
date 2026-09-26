using System.Security.Cryptography;
using SkiaSharp;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Enforces the exact Microsoft Store screenshot handoff and its file-system safety boundary.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// </remarks>
internal static class StoreListingScreenshotValidator
{
    private const long MaximumPngBytes = 50L * 1024 * 1024;
    private const int MinimumDesktopWidth = 1366;
    private const int MinimumDesktopHeight = 768;

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly IReadOnlyDictionary<string, (int Width, int Height)> ExpectedScreenshots =
        new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal)
        {
            ["ru-RU/suspected.png"] = (1380, 1860),
            ["ru-RU/recording.png"] = (1380, 1860),
            ["ru-RU/ready.png"] = (1380, 1860),
            ["ru-RU/ask.png"] = (1960, 890),
            ["en-US/suspected.png"] = (1380, 1860),
            ["en-US/recording.png"] = (1380, 1860),
            ["en-US/ready.png"] = (1380, 1860),
            ["en-US/ask.png"] = (1960, 890)
        };

    public static void Validate(
        string outputRoot,
        IReadOnlyList<StoreListingScreenshot> screenshots)
    {
        var root = Path.GetFullPath(outputRoot);
        EnsurePlainEntry(root);
        if (screenshots.Count != ExpectedScreenshots.Count)
        {
            throw new InvalidDataException(
                $"Expected exactly {ExpectedScreenshots.Count} Store screenshots, found {screenshots.Count}.");
        }

        var manifestPaths = screenshots
            .Select(static screenshot => screenshot.RelativePath)
            .ToArray();
        if (manifestPaths.Distinct(StringComparer.Ordinal).Count() != ExpectedScreenshots.Count
            || !manifestPaths.ToHashSet(StringComparer.Ordinal).SetEquals(ExpectedScreenshots.Keys))
        {
            throw new InvalidDataException(
                "Store screenshot manifest does not match the exact bilingual screenshot allowlist.");
        }

        var diskFiles = EnumeratePlainTree(root)
            .Where(static path => !Directory.Exists(path))
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedDiskFiles = ExpectedScreenshots.Keys
            .Append("manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!diskFiles.SequenceEqual(expectedDiskFiles, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Store listing output must contain exactly eight allowlisted PNGs and manifest.json. Found: "
                + string.Join(", ", diskFiles));
        }

        var directories = EnumeratePlainTree(root)
            .Where(Directory.Exists)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!directories.SequenceEqual(new[] { "en-US", "ru-RU" }, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Store listing output must contain only the en-US and ru-RU screenshot directories.");
        }

        foreach (var screenshot in screenshots)
        {
            var absolutePath = Path.GetFullPath(Path.Combine(
                root,
                screenshot.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureChildPath(root, absolutePath);
            EnsurePlainEntry(absolutePath);
            var fileInfo = new FileInfo(absolutePath);
            if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.Directory) != 0)
            {
                throw new FileNotFoundException("Store screenshot is not a regular file.", absolutePath);
            }

            if (!string.Equals(fileInfo.Extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{screenshot.RelativePath}' is not a PNG file.");
            }

            if (fileInfo.Length is <= 0 or > MaximumPngBytes || fileInfo.Length != screenshot.ByteLength)
            {
                throw new InvalidDataException(
                    $"'{screenshot.RelativePath}' has invalid or mismatched size {fileInfo.Length} bytes.");
            }

            using (var stream = fileInfo.OpenRead())
            {
                var signature = new byte[PngSignature.Length];
                if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(PngSignature))
                {
                    throw new InvalidDataException($"'{screenshot.RelativePath}' is not a valid PNG stream.");
                }
            }

            using var bitmap = SKBitmap.Decode(absolutePath)
                ?? throw new InvalidDataException($"'{screenshot.RelativePath}' could not be decoded.");
            var expected = ExpectedScreenshots[screenshot.RelativePath];
            if (bitmap.Width != expected.Width
                || bitmap.Height != expected.Height
                || screenshot.PixelWidth != expected.Width
                || screenshot.PixelHeight != expected.Height)
            {
                throw new InvalidDataException(
                    $"'{screenshot.RelativePath}' is {bitmap.Width}×{bitmap.Height}; "
                    + $"expected {expected.Width}×{expected.Height}.");
            }

            if (bitmap.Width < MinimumDesktopWidth || bitmap.Height < MinimumDesktopHeight)
            {
                throw new InvalidDataException(
                    $"'{screenshot.RelativePath}' is below the 1366×768 Desktop Store minimum.");
            }

            if (CountSampledColors(bitmap) < 8)
            {
                throw new InvalidDataException($"'{screenshot.RelativePath}' appears visually empty.");
            }

            if (HasTransparentPixels(bitmap))
            {
                throw new InvalidDataException(
                    $"'{screenshot.RelativePath}' contains transparent pixels and is unsafe for Store compositing.");
            }

            if (HasUnsafeOuterBand(bitmap))
            {
                throw new InvalidDataException(
                    $"'{screenshot.RelativePath}' has a transparent, dark or uneven outer band in the declared light theme.");
            }

            var actualSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath)))
                .ToLowerInvariant();
            if (!string.Equals(actualSha256, screenshot.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"'{screenshot.RelativePath}' SHA-256 does not match its manifest.");
            }
        }

        foreach (var state in new[] { "suspected", "recording", "ready", "ask" })
        {
            var languagePair = screenshots
                .Where(screenshot => string.Equals(screenshot.State, state, StringComparison.Ordinal))
                .ToArray();
            if (languagePair.Length != 2
                || languagePair.Select(static screenshot => screenshot.Language)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != 2
                || languagePair.Select(static screenshot => screenshot.Sha256)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != 2)
            {
                throw new InvalidDataException(
                    $"RU/EN Store screenshots are missing or visually identical for '{state}'.");
            }
        }
    }

    private static IReadOnlyList<string> EnumeratePlainTree(string root)
    {
        var entries = new List<string>();
        VisitDirectory(root, entries);
        return entries;
    }

    private static void VisitDirectory(string directory, ICollection<string> entries)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsurePlainEntry(entry);
            entries.Add(entry);
            if (Directory.Exists(entry))
            {
                VisitDirectory(entry, entries);
            }
        }
    }

    private static void EnsurePlainEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Reparse points are forbidden in Store listing output: '{path}'.");
        }
    }

    private static void EnsureChildPath(string root, string path)
    {
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Store screenshot path escaped the requested output root.");
        }
    }

    private static int CountSampledColors(SKBitmap bitmap)
    {
        var colors = new HashSet<uint>();
        var stepX = Math.Max(1, bitmap.Width / 64);
        var stepY = Math.Max(1, bitmap.Height / 64);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                colors.Add((uint)bitmap.GetPixel(x, y));
                if (colors.Count >= 8)
                {
                    return colors.Count;
                }
            }
        }

        return colors.Count;
    }

    private static bool HasTransparentPixels(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != byte.MaxValue)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasUnsafeOuterBand(SKBitmap bitmap)
    {
        const int bandWidth = 36;
        var background = bitmap.GetPixel(0, 0);
        if (!IsLightOpaqueBackground(background))
        {
            return true;
        }

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var isOuterBand = x < bandWidth
                    || x >= bitmap.Width - bandWidth
                    || y < bandWidth
                    || y >= bitmap.Height - bandWidth;
                if (isOuterBand && !MatchesBackground(bitmap.GetPixel(x, y), background))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsLightOpaqueBackground(SKColor color) =>
        color.Alpha == byte.MaxValue
        && color.Red >= 200
        && color.Green >= 200
        && color.Blue >= 200;

    private static bool MatchesBackground(SKColor color, SKColor background) =>
        color.Alpha == byte.MaxValue
        && Math.Abs(color.Red - background.Red) <= 2
        && Math.Abs(color.Green - background.Green) <= 2
        && Math.Abs(color.Blue - background.Blue) <= 2;

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');
}

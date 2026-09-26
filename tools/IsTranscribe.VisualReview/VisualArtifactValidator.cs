using SkiaSharp;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Enforces PNG integrity, expected DPI dimensions and meaningful visual variation.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// </remarks>
internal static class VisualArtifactValidator
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly IReadOnlyList<string> ExpectedLanguages = ["ru", "en"];

    public static void Validate(
        string outputRoot,
        IReadOnlyList<ReviewArtifact> artifacts,
        IReadOnlyList<TokenContrastCheck> contrastChecks)
    {
        var screens = artifacts.Where(static artifact => artifact.Kind == ReviewArtifactKind.Screen).ToArray();
        var actualLanguages = screens
            .Select(static artifact => artifact.Language)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static language => language, StringComparer.Ordinal)
            .ToArray();
        if (!actualLanguages.SequenceEqual(
                ExpectedLanguages.OrderBy(static language => language, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Expected language matrices [{string.Join(", ", ExpectedLanguages)}], "
                + $"found [{string.Join(", ", actualLanguages)}].");
        }

        var contactSheets = artifacts
            .Where(static artifact => artifact.Kind == ReviewArtifactKind.ContactSheet)
            .ToArray();
        foreach (var language in ExpectedLanguages)
        {
            var languageScreens = screens
                .Where(artifact => artifact.Language == language)
                .ToArray();
            var languageCanonicalScreens = languageScreens
                .Where(static artifact => artifact.Suite == ReviewArtifactSuite.Canonical)
                .ToArray();
            var edgeCaseScreens = languageScreens
                .Where(static artifact => artifact.Suite == ReviewArtifactSuite.EdgeCase)
                .ToArray();
            if (languageCanonicalScreens.Length != 71 || edgeCaseScreens.Length != 10)
            {
                throw new InvalidDataException(
                    $"Expected 71 canonical and 10 edge-case PNGs for '{language}', "
                    + $"found {languageCanonicalScreens.Length} and {edgeCaseScreens.Length}.");
            }

            var languageContactSheets = contactSheets
                .Where(artifact => artifact.Language == language)
                .ToArray();
            if (languageContactSheets.Count(static artifact =>
                    artifact.Suite == ReviewArtifactSuite.Canonical) != 8
                || languageContactSheets.Count(static artifact =>
                    artifact.Suite == ReviewArtifactSuite.EdgeCase) != 1
                || languageContactSheets.Count(static artifact =>
                    artifact.Theme == "high-contrast") != 1)
            {
                throw new InvalidDataException(
                    $"Expected eight canonical and one edge-case contact sheet for '{language}', "
                    + $"found {languageContactSheets.Length} total.");
            }

            var highContrastScreens = languageCanonicalScreens
                .Where(static artifact => artifact.Theme == "high-contrast")
                .ToArray();
            if (highContrastScreens.Length != 13)
            {
                throw new InvalidDataException(
                    $"Expected 13 high-contrast screen PNGs for '{language}', "
                    + $"found {highContrastScreens.Length}.");
            }

            if (edgeCaseScreens.Count(static artifact => artifact.Surface == "main") != 6
                || edgeCaseScreens.Count(static artifact => artifact.Surface == "setup") != 2
                || edgeCaseScreens.Count(static artifact => artifact.Surface == "settings") != 2)
            {
                throw new InvalidDataException(
                    $"The bounded edge-case matrix is incomplete for '{language}'.");
            }

            if (edgeCaseScreens.Count(static artifact =>
                    artifact.State == "recording-microphone-unavailable") != 3
                || edgeCaseScreens.Count(static artifact =>
                    artifact.State == "legacy-transcript-action") != 3)
            {
                throw new InvalidDataException(
                    $"The main-window edge-case matrix is incomplete for '{language}'.");
            }
        }

        var canonicalScreens = screens
            .Where(static artifact => artifact.Suite == ReviewArtifactSuite.Canonical)
            .ToArray();

        if (contrastChecks.Count != 48 || contrastChecks.Any(static check => !check.Passed))
        {
            var failures = contrastChecks
                .Where(static check => !check.Passed)
                .Select(static check =>
                    $"{check.Theme}: {check.ForegroundRole}/{check.BackgroundRole}={check.Ratio:0.00}:1");
            throw new InvalidDataException(
                "Semantic text contrast failed: " + string.Join(", ", failures));
        }

        foreach (var artifact in artifacts)
        {
            var expectedPrefix = $"languages/{artifact.Language}/";
            if (!ExpectedLanguages.Contains(artifact.Language, StringComparer.Ordinal)
                || !artifact.RelativePath.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"'{artifact.RelativePath}' is not rooted in its '{artifact.Language}' language directory.");
            }

            var path = Path.Combine(outputRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Visual artifact is missing.", path);
            }

            using (var stream = File.OpenRead(path))
            {
                var signature = new byte[PngSignature.Length];
                if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(PngSignature))
                {
                    throw new InvalidDataException($"'{artifact.RelativePath}' is not a valid PNG stream.");
                }
            }

            using var bitmap = SKBitmap.Decode(path)
                ?? throw new InvalidDataException($"'{artifact.RelativePath}' could not be decoded.");
            if (bitmap.Width != artifact.PixelWidth || bitmap.Height != artifact.PixelHeight)
            {
                throw new InvalidDataException($"'{artifact.RelativePath}' dimensions do not match its manifest entry.");
            }

            if (CountSampledColors(bitmap) < 8)
            {
                throw new InvalidDataException($"'{artifact.RelativePath}' appears visually empty.");
            }
        }

        foreach (var main in screens.Where(static artifact => artifact.Surface == "main"))
        {
            var expectedWidth = (int)Math.Round(460 * (main.ScalePercent / 100d));
            var expectedHeight = (int)Math.Round(620 * (main.ScalePercent / 100d));
            if (main.PixelWidth != expectedWidth || main.PixelHeight != expectedHeight)
            {
                throw new InvalidDataException(
                    $"'{main.RelativePath}' is {main.PixelWidth}×{main.PixelHeight}; expected {expectedWidth}×{expectedHeight}.");
            }
        }

        foreach (var stateGroup in canonicalScreens
                     .Where(static artifact => artifact.Surface == "main")
                     .Where(static artifact => artifact.Theme is "light" or "dark")
                     .GroupBy(static artifact => (
                         artifact.Language,
                         artifact.State,
                         artifact.ScalePercent)))
        {
            if (stateGroup.Select(static artifact => artifact.Sha256).Distinct(StringComparer.Ordinal).Count() != 2)
            {
                throw new InvalidDataException(
                    $"Light and dark renders are identical for '{stateGroup.Key.Language}' "
                    + $"'{stateGroup.Key.State}' at {stateGroup.Key.ScalePercent}%.");
            }
        }

        foreach (var matrix in canonicalScreens
                     .Where(static artifact => artifact.Surface == "main")
                     .GroupBy(static artifact => (
                         artifact.Language,
                         artifact.Theme,
                         artifact.ScalePercent)))
        {
            if (matrix.Select(static artifact => artifact.Sha256).Distinct(StringComparer.Ordinal).Count() != 8)
            {
                throw new InvalidDataException(
                    $"Canonical states are not visually distinct for '{matrix.Key.Language}' "
                    + $"{matrix.Key.Theme} at {matrix.Key.ScalePercent}%.");
            }
        }

        foreach (var languagePair in screens.GroupBy(static artifact => (
                     artifact.Surface,
                     artifact.State,
                     artifact.Theme,
                     artifact.ScalePercent,
                     artifact.Suite)))
        {
            if (languagePair.Count() != ExpectedLanguages.Count
                || languagePair.Select(static artifact => artifact.Language)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != ExpectedLanguages.Count
                || languagePair.Select(static artifact => artifact.Sha256)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != ExpectedLanguages.Count)
            {
                throw new InvalidDataException(
                    $"RU/EN renders are missing or visually identical for "
                    + $"'{languagePair.Key.Surface}/{languagePair.Key.State}' "
                    + $"({languagePair.Key.Theme}, {languagePair.Key.ScalePercent}%).");
            }
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

}

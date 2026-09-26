using SkiaSharp;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Produces review-sized matrices that make state and theme inconsistencies visible at a glance.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// </remarks>
internal static class ContactSheetWriter
{
    private const int SheetPadding = 24;
    private const int HeaderHeight = 52;
    private const int CellWidth = 248;
    private const int CellHeight = 356;
    private const int CellPadding = 12;
    private const int LabelHeight = 30;

    public static IReadOnlyList<ReviewArtifact> Create(
        string outputRoot,
        IReadOnlyList<ReviewArtifact> artifacts)
    {
        var results = new List<ReviewArtifact>();
        foreach (var group in artifacts
                     .Where(static artifact => artifact.Kind == ReviewArtifactKind.Screen
                         && artifact.Suite == ReviewArtifactSuite.Canonical
                         && artifact.Surface == "main"
                         && artifact.Theme != "high-contrast")
                     .GroupBy(static artifact => (
                         artifact.Language,
                         artifact.Theme,
                         artifact.ScalePercent))
                     .OrderBy(static group => group.Key.Language, StringComparer.Ordinal)
                     .ThenBy(static group => ThemeOrder(group.Key.Theme))
                     .ThenBy(static group => group.Key.ScalePercent))
        {
            var ordered = CanonicalScenarioCatalog.MainScenarios
                .Select(scenario => group.Single(artifact => artifact.State == scenario.Id))
                .ToArray();
            var relativePath = Path.Combine(
                "languages",
                group.Key.Language,
                "contact-sheets",
                $"main-{group.Key.Theme}-{group.Key.ScalePercent:D3}.png");
            results.Add(CreateSheet(
                outputRoot,
                relativePath,
                $"Main · {group.Key.Language.ToUpperInvariant()} · {group.Key.Theme} · {group.Key.ScalePercent}%",
                group.Key.Theme,
                group.Key.Language,
                group.Key.ScalePercent,
                "main-contact-sheet",
                ordered,
                columns: 4));
        }

        var languages = artifacts
            .Where(static artifact => artifact.Kind == ReviewArtifactKind.Screen)
            .Select(static artifact => artifact.Language)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static language => language, StringComparer.Ordinal)
            .ToArray();
        foreach (var language in languages)
        {
            var auxiliary = artifacts
                .Where(artifact => artifact.Kind == ReviewArtifactKind.Screen
                    && artifact.Suite == ReviewArtifactSuite.Canonical
                    && artifact.Language == language
                    && artifact.Surface != "main"
                    && artifact.Theme != "high-contrast")
                .OrderBy(static artifact => ThemeOrder(artifact.Theme))
                .ThenBy(static artifact => AuxiliaryOrder(artifact.Surface))
                .ToArray();
            results.Add(CreateSheet(
                outputRoot,
                LanguageSheetPath(language, "auxiliary-light-dark.png"),
                $"Auxiliary surfaces · {language.ToUpperInvariant()} · light / dark",
                "mixed",
                language,
                scalePercent: 100,
                "auxiliary-contact-sheet",
                auxiliary,
                columns: 4));

            var highContrast = artifacts
                .Where(artifact => artifact.Kind == ReviewArtifactKind.Screen
                    && artifact.Suite == ReviewArtifactSuite.Canonical
                    && artifact.Language == language
                    && artifact.Theme == "high-contrast")
                .OrderBy(static artifact => artifact.Surface == "main" ? 0 : 1)
                .ThenBy(static artifact => artifact.Surface == "main"
                    ? MainStateOrder(artifact.State)
                    : AuxiliaryOrder(artifact.Surface))
                .ToArray();
            results.Add(CreateSheet(
                outputRoot,
                LanguageSheetPath(language, "high-contrast-100.png"),
                $"High contrast · {language.ToUpperInvariant()} · main and auxiliary · 100%",
                "high-contrast",
                language,
                scalePercent: 100,
                "high-contrast-contact-sheet",
                highContrast,
                columns: 4));

            var edgeCases = artifacts
                .Where(artifact => artifact.Kind == ReviewArtifactKind.Screen
                    && artifact.Suite == ReviewArtifactSuite.EdgeCase
                    && artifact.Language == language)
                .OrderBy(static artifact => EdgeSurfaceOrder(artifact.Surface))
                .ThenBy(static artifact => ThemeOrder(artifact.Theme))
                .ToArray();
            results.Add(CreateSheet(
                outputRoot,
                LanguageSheetPath(language, "edge-cases.png"),
                $"Edge cases · {language.ToUpperInvariant()} · 100%",
                "mixed",
                language,
                scalePercent: 100,
                "edge-case-contact-sheet",
                edgeCases,
                columns: 4,
                suite: ReviewArtifactSuite.EdgeCase));
        }

        return results;
    }

    private static ReviewArtifact CreateSheet(
        string outputRoot,
        string relativePath,
        string title,
        string theme,
        string language,
        int scalePercent,
        string surface,
        IReadOnlyList<ReviewArtifact> items,
        int columns,
        ReviewArtifactSuite suite = ReviewArtifactSuite.Canonical)
    {
        var rows = (int)Math.Ceiling(items.Count / (double)columns);
        var width = (SheetPadding * 2) + (CellWidth * columns);
        var height = (SheetPadding * 2) + HeaderHeight + (CellHeight * rows);
        var dark = theme is "dark" or "high-contrast";
        var background = dark ? new SKColor(24, 24, 22) : new SKColor(238, 236, 230);
        var cellBackground = dark ? new SKColor(38, 38, 35) : SKColors.White;
        var foreground = dark ? new SKColor(242, 241, 236) : new SKColor(32, 32, 30);
        var border = dark ? new SKColor(72, 72, 68) : new SKColor(208, 205, 197);

        using var bitmap = new SKBitmap(width, height, isOpaque: true);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        using var typeface = SKTypeface.FromFamilyName("Inter") ?? SKTypeface.Default;
        using var titleFont = new SKFont(typeface, 22);
        using var labelFont = new SKFont(typeface, 15);
        using var titlePaint = new SKPaint { Color = foreground, IsAntialias = true };
        using var borderPaint = new SKPaint
        {
            Color = border,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        using var imagePaint = new SKPaint { IsAntialias = true };

        canvas.DrawText(title, SheetPadding, SheetPadding + 28, titleFont, titlePaint);
        for (var index = 0; index < items.Count; index++)
        {
            var artifact = items[index];
            var row = index / columns;
            var column = index % columns;
            var cellX = SheetPadding + (column * CellWidth);
            var cellY = SheetPadding + HeaderHeight + (row * CellHeight);
            var cellRect = new SKRect(cellX + 4, cellY + 4, cellX + CellWidth - 4, cellY + CellHeight - 4);
            using var cellPaint = new SKPaint { Color = cellBackground, IsAntialias = true };
            canvas.DrawRoundRect(cellRect, 10, 10, cellPaint);
            canvas.DrawRoundRect(cellRect, 10, 10, borderPaint);

            var label = artifact.Suite == ReviewArtifactSuite.EdgeCase
                ? EdgeCaseLabel(artifact)
                : artifact.State is null
                    ? $"{artifact.Theme} · {artifact.Surface}"
                    : artifact.State;
            canvas.DrawText(
                label,
                cellX + CellPadding,
                cellY + CellPadding + 17,
                labelFont,
                titlePaint);

            var absolutePath = Path.Combine(outputRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            using var screenshot = SKBitmap.Decode(absolutePath)
                ?? throw new InvalidDataException($"Could not decode '{artifact.RelativePath}'.");
            var availableWidth = CellWidth - (CellPadding * 2);
            var availableHeight = CellHeight - LabelHeight - (CellPadding * 2);
            var fit = Math.Min(
                availableWidth / (double)screenshot.Width,
                availableHeight / (double)screenshot.Height);
            var renderWidth = (float)(screenshot.Width * fit);
            var renderHeight = (float)(screenshot.Height * fit);
            var left = (float)(cellX + ((CellWidth - renderWidth) / 2));
            var top = (float)(cellY + LabelHeight + CellPadding);
            var destination = new SKRect(left, top, left + renderWidth, top + renderHeight);
            canvas.DrawBitmap(screenshot, destination, imagePaint);
        }

        var absoluteOutputPath = Path.Combine(outputRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutputPath)!);
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(absoluteOutputPath))
        {
            data.SaveTo(stream);
        }

        return ReviewArtifact.Create(
            ReviewArtifactKind.ContactSheet,
            surface,
            state: null,
            theme,
            language,
            scalePercent,
            width,
            height,
            width,
            height,
            relativePath.Replace('\\', '/'),
            absoluteOutputPath,
            suite);
    }

    private static string LanguageSheetPath(string language, string fileName) =>
        Path.Combine("languages", language, "contact-sheets", fileName);

    private static int AuxiliaryOrder(string surface) => surface switch
    {
        "setup" => 0,
        "settings" => 1,
        "diagnostics" => 2,
        "ask" => 3,
        "confirmation" => 4,
        _ => 5
    };

    private static int MainStateOrder(string? state)
    {
        for (var index = 0; index < CanonicalScenarioCatalog.MainScenarios.Count; index++)
        {
            if (CanonicalScenarioCatalog.MainScenarios[index].Id == state)
            {
                return index;
            }
        }

        return CanonicalScenarioCatalog.MainScenarios.Count;
    }

    private static int EdgeSurfaceOrder(string surface) => surface switch
    {
        "main" => 0,
        "setup" => 1,
        "settings" => 2,
        _ => 3
    };

    private static int ThemeOrder(string theme) => theme switch
    {
        "light" => 0,
        "dark" => 1,
        "high-contrast" => 2,
        _ => 3
    };

    private static string EdgeCaseLabel(ReviewArtifact artifact) => (artifact.Surface, artifact.State) switch
    {
        ("main", "legacy-transcript-action") => $"{artifact.Theme} · main · legacy text",
        ("main", _) => $"{artifact.Theme} · main · mic off",
        ("setup", _) => $"{artifact.Theme} · setup · mic missing",
        ("settings", _) => $"{artifact.Theme} · settings · mic missing",
        _ => $"{artifact.Theme} · {artifact.Surface}"
    };
}

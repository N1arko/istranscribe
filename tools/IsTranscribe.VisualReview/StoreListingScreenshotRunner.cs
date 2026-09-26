using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Theming;
using IsTranscribe.Desktop.ViewModels;
using IsTranscribe.Desktop.Views;
using SkiaSharp;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Produces a deterministic, submission-ready screenshot set from production windows and view models.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// </remarks>
internal static class StoreListingScreenshotRunner
{
    private const string Specification =
        "spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission";

    private static readonly IReadOnlyList<StoreListingLanguage> Languages =
    [
        new("ru-RU", "ru", UiLanguage.Russian),
        new("en-US", "en", UiLanguage.English)
    ];

    private static readonly IReadOnlyList<string> MainScenarioIds =
    [
        "suspected",
        "recording",
        "ready"
    ];

    private static readonly ReviewScale MainScale = new(300, 3.0);
    private static readonly ReviewScale AskScale = new(500, 5.0);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task RunAsync(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var application = Avalonia.Application.Current as ReviewApplication
            ?? throw new InvalidOperationException("ReviewApplication was not initialized.");
        var root = PrepareOutput(outputRoot);
        var timeProvider = new FixedTimeProvider(CanonicalScenarioCatalog.ClockUtc);
        var shell = new ReviewDesktopShell();
        var screenshots = new List<StoreListingScreenshot>();

        foreach (var language in Languages)
        {
            application.Localization.SetLanguage(language.Value);
            application.Themes.SetTheme(UiThemeMode.Light);
            foreach (var scenarioId in MainScenarioIds)
            {
                var scenario = CanonicalScenarioCatalog.MainScenarios.Single(
                    candidate => string.Equals(candidate.Id, scenarioId, StringComparison.Ordinal));
                screenshots.Add(await RenderMainWindowAsync(
                    root,
                    application.Localization,
                    language,
                    scenario,
                    timeProvider,
                    shell));
            }

            screenshots.Add(await RenderAskPromptAsync(
                root,
                application.Localization,
                language,
                timeProvider));
        }

        WriteManifest(root, screenshots);
        StoreListingScreenshotValidator.Validate(root, screenshots);
        application.Localization.SetLanguage(UiLanguage.Russian);

        Console.WriteLine(
            $"Rendered {screenshots.Count} Store-listing screenshots from production surfaces.");
        Console.WriteLine($"Store listing assets: {root}");
    }

    private static async Task<StoreListingScreenshot> RenderMainWindowAsync(
        string outputRoot,
        LocalizationService localization,
        StoreListingLanguage language,
        CanonicalMainScenario scenario,
        TimeProvider timeProvider,
        ReviewDesktopShell shell)
    {
        var snapshot = CanonicalScenarioCatalog.WithThemeAndLanguage(
            scenario.Snapshot,
            "light",
            language.RuntimeId);
        await using var runtime = new ReviewApplicationRuntime(snapshot);
        using var viewModel = new MainWindowViewModel(runtime, shell, localization, timeProvider);
        await viewModel.InitializeAsync(CancellationToken.None);
        var window = new MainWindow { DataContext = viewModel };

        try
        {
            return Capture(
                outputRoot,
                Path.Combine(language.CultureName, $"{scenario.Id}.png"),
                window,
                MainScale,
                language.CultureName,
                surface: "main",
                state: scenario.Id);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<StoreListingScreenshot> RenderAskPromptAsync(
        string outputRoot,
        LocalizationService localization,
        StoreListingLanguage language,
        TimeProvider timeProvider)
    {
        var snapshot = CanonicalScenarioCatalog.WithThemeAndLanguage(
            CanonicalScenarioCatalog.MainScenarios[0].Snapshot,
            "light",
            language.RuntimeId);
        await using var runtime = new ReviewApplicationRuntime(snapshot);
        using var viewModel = new AskPromptViewModel(
            runtime,
            localization,
            new MeetingPromptSnapshot(
                "store-listing-ask",
                "google-meet",
                "Google Meet · Zen Browser",
                CanonicalScenarioCatalog.ClockUtc - TimeSpan.FromSeconds(8),
                CanonicalScenarioCatalog.ClockUtc + TimeSpan.FromSeconds(22),
                ConfidenceScore: 86,
                DecisionReasons: ["conversation_detected", "meeting_application_active"]),
            timeProvider,
            showAdvancedDiagnostics: false);
        var window = new AskPromptWindow(viewModel)
        {
            SizeToContent = SizeToContent.Manual,
            Height = 178
        };

        try
        {
            return Capture(
                outputRoot,
                Path.Combine(language.CultureName, "ask.png"),
                window,
                AskScale,
                language.CultureName,
                surface: "ask",
                state: "ask");
        }
        finally
        {
            window.CloseWithoutResolution();
        }
    }

    private static StoreListingScreenshot Capture(
        string outputRoot,
        string relativePath,
        Window window,
        ReviewScale scale,
        string language,
        string surface,
        string state)
    {
        var background = ResolveOpaqueBackground(window);
        window.Background = new SolidColorBrush(background);
        window.Show();
        window.SetRenderScaling(scale.Factor);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(8);
        using var bitmap = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException($"'{surface}' did not produce a rendered frame.");
        var absolutePath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
        if (!absolutePath.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Store screenshot path escaped the requested output root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        SaveOpaquePng(bitmap, background, absolutePath);
        var fileInfo = new FileInfo(absolutePath);
        return new StoreListingScreenshot(
            Language: language,
            Surface: surface,
            State: state,
            ScalePercent: scale.Percent,
            LogicalWidth: Math.Round(window.Bounds.Width, 2),
            LogicalHeight: Math.Round(window.Bounds.Height, 2),
            PixelWidth: bitmap.PixelSize.Width,
            PixelHeight: bitmap.PixelSize.Height,
            RelativePath: NormalizeRelativePath(relativePath),
            ByteLength: fileInfo.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath)))
                .ToLowerInvariant());
    }

    private static Color ResolveOpaqueBackground(Window window)
    {
        if (window.Background is not ISolidColorBrush brush)
        {
            throw new InvalidDataException(
                $"'{window.GetType().Name}' must resolve an opaque solid production background for Store capture.");
        }

        return Color.FromArgb(byte.MaxValue, brush.Color.R, brush.Color.G, brush.Color.B);
    }

    private static void SaveOpaquePng(
        Bitmap renderedFrame,
        Color background,
        string absolutePath)
    {
        using var renderedPng = new MemoryStream();
        renderedFrame.Save(renderedPng, new PngBitmapEncoderOptions());
        renderedPng.Position = 0;
        using var renderedBitmap = SKBitmap.Decode(renderedPng)
            ?? throw new InvalidDataException("The rendered production frame could not be decoded for Store compositing.");
        using var opaqueBitmap = new SKBitmap(new SKImageInfo(
            renderedBitmap.Width,
            renderedBitmap.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(opaqueBitmap))
        {
            canvas.Clear(new SKColor(background.R, background.G, background.B, byte.MaxValue));
            canvas.DrawBitmap(renderedBitmap, 0, 0);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(opaqueBitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new InvalidDataException("The opaque Store screenshot could not be encoded as PNG.");
        using var file = File.Create(absolutePath);
        encoded.SaveTo(file);
    }

    private static string PrepareOutput(string outputRoot)
    {
        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        EnsurePlainEntry(root);

        foreach (var managedName in Languages.Select(static language => language.CultureName))
        {
            var managedPath = Path.GetFullPath(Path.Combine(root, managedName));
            EnsureChildPath(root, managedPath);
            if (Directory.Exists(managedPath))
            {
                EnsurePlainTree(managedPath);
                Directory.Delete(managedPath, recursive: true);
            }
            else if (File.Exists(managedPath))
            {
                throw new InvalidDataException($"Managed Store listing path is a file: '{managedPath}'.");
            }
        }

        var manifestPath = Path.GetFullPath(Path.Combine(root, "manifest.json"));
        EnsureChildPath(root, manifestPath);
        if (File.Exists(manifestPath))
        {
            EnsurePlainEntry(manifestPath);
            File.Delete(manifestPath);
        }
        else if (Directory.Exists(manifestPath))
        {
            throw new InvalidDataException($"Managed Store listing manifest is a directory: '{manifestPath}'.");
        }

        var unmanagedEntries = Directory.EnumerateFileSystemEntries(root).ToArray();
        if (unmanagedEntries.Length != 0)
        {
            throw new InvalidDataException(
                "Store listing output contains unmanaged entries: "
                + string.Join(", ", unmanagedEntries.Select(Path.GetFileName)));
        }

        return root;
    }

    private static void WriteManifest(
        string outputRoot,
        IReadOnlyList<StoreListingScreenshot> screenshots)
    {
        var manifest = new StoreListingScreenshotManifest(
            SchemaVersion: 1,
            Spec: Specification,
            CanonicalClockUtc: CanonicalScenarioCatalog.ClockUtc,
            RenderPlatform: "windows",
            AvaloniaVersion: "12.1.0",
            Theme: "light",
            ScreenshotCount: screenshots.Count,
            Screenshots: screenshots);
        File.WriteAllText(
            Path.Combine(outputRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void EnsurePlainTree(string root)
    {
        EnsurePlainEntry(root);
        EnsurePlainTreeChildren(root);
    }

    private static void EnsurePlainTreeChildren(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsurePlainEntry(entry);
            if (Directory.Exists(entry))
            {
                EnsurePlainTreeChildren(entry);
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
            throw new InvalidOperationException("Managed Store listing path escaped the requested output root.");
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');
}

internal sealed record StoreListingLanguage(
    string CultureName,
    string RuntimeId,
    UiLanguage Value);

internal sealed record StoreListingScreenshot(
    string Language,
    string Surface,
    string State,
    int ScalePercent,
    double LogicalWidth,
    double LogicalHeight,
    int PixelWidth,
    int PixelHeight,
    string RelativePath,
    long ByteLength,
    string Sha256);

internal sealed record StoreListingScreenshotManifest(
    int SchemaVersion,
    string Spec,
    DateTimeOffset CanonicalClockUtc,
    string RenderPlatform,
    string AvaloniaVersion,
    string Theme,
    int ScreenshotCount,
    IReadOnlyList<StoreListingScreenshot> Screenshots);

using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Guards the deterministic, production-surface Store screenshot handoff.
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// </summary>
public sealed class WindowsStoreListingScreenshotToolingContractTests
{
    [Fact]
    public void StoreCaptureIsAnExplicitSuiteThatCannotPolluteCanonicalVisualReview()
    {
        var commandLine = Read("ReviewCommandLine.cs");
        var application = Read("ReviewApplication.axaml.cs");
        var canonicalRunner = Read("VisualReviewRunner.cs");

        Assert.Contains("--store-listing-output", commandLine, StringComparison.Ordinal);
        Assert.Contains("select separate artifact suites and cannot be combined", commandLine, StringComparison.Ordinal);
        Assert.Contains("IsStoreListingCapture", application, StringComparison.Ordinal);
        Assert.Contains("StoreListingScreenshotRunner.RunAsync", application, StringComparison.Ordinal);
        Assert.Contains("new(100, 1.0)", canonicalRunner, StringComparison.Ordinal);
        Assert.Contains("new(150, 1.5)", canonicalRunner, StringComparison.Ordinal);
        Assert.Contains("new(200, 2.0)", canonicalRunner, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreListingScreenshot", canonicalRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreCaptureUsesExactBilingualProductionSurfaceMatrix()
    {
        var runner = Read("StoreListingScreenshotRunner.cs");

        Assert.Contains("new(\"ru-RU\", \"ru\", UiLanguage.Russian)", runner, StringComparison.Ordinal);
        Assert.Contains("new(\"en-US\", \"en\", UiLanguage.English)", runner, StringComparison.Ordinal);
        Assert.Contains("\"suspected\"", runner, StringComparison.Ordinal);
        Assert.Contains("\"recording\"", runner, StringComparison.Ordinal);
        Assert.Contains("\"ready\"", runner, StringComparison.Ordinal);
        Assert.Contains("new(300, 3.0)", runner, StringComparison.Ordinal);
        Assert.Contains("new(500, 5.0)", runner, StringComparison.Ordinal);
        Assert.Contains("new MainWindow { DataContext = viewModel }", runner, StringComparison.Ordinal);
        Assert.Contains("new AskPromptWindow(viewModel)", runner, StringComparison.Ordinal);
        Assert.Contains("Google Meet · Zen Browser", runner, StringComparison.Ordinal);
        Assert.Contains("CanonicalScenarioCatalog.ClockUtc", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreValidatorIsFailClosedForDimensionsSizePngIntegrityAndReparsePoints()
    {
        var validator = Read("StoreListingScreenshotValidator.cs");

        foreach (var culture in new[] { "ru-RU", "en-US" })
        {
            foreach (var state in new[] { "suspected", "recording", "ready", "ask" })
            {
                Assert.Contains($"[\"{culture}/{state}.png\"]", validator, StringComparison.Ordinal);
            }
        }

        Assert.Contains("ExpectedScreenshots.Count", validator, StringComparison.Ordinal);
        Assert.Contains("50L * 1024 * 1024", validator, StringComparison.Ordinal);
        Assert.Contains("1366", validator, StringComparison.Ordinal);
        Assert.Contains("768", validator, StringComparison.Ordinal);
        Assert.Contains("PngSignature", validator, StringComparison.Ordinal);
        Assert.Contains("SKBitmap.Decode", validator, StringComparison.Ordinal);
        Assert.Contains("HasTransparentPixels", validator, StringComparison.Ordinal);
        Assert.Contains("HasUnsafeOuterBand", validator, StringComparison.Ordinal);
        Assert.Contains("const int bandWidth = 36", validator, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", validator, StringComparison.Ordinal);
        Assert.Contains("SHA-256 does not match its manifest", validator, StringComparison.Ordinal);
        Assert.Contains("RU/EN Store screenshots are missing or visually identical", validator, StringComparison.Ordinal);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "tools",
        "IsTranscribe.VisualReview",
        name));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}

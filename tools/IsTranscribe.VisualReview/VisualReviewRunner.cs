using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Theming;
using IsTranscribe.Desktop.ViewModels;
using IsTranscribe.Desktop.Views;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Renders production windows against canonical state without starting platform services.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// </remarks>
internal static class VisualReviewRunner
{
    private static readonly IReadOnlyList<ReviewLanguage> Languages =
    [
        new("ru", "ru-RU", UiLanguage.Russian),
        new("en", "en-US", UiLanguage.English)
    ];

    private static readonly IReadOnlyList<ReviewScale> Scales =
    [
        new(100, 1.0),
        new(150, 1.5),
        new(200, 2.0)
    ];

    private static readonly IReadOnlyList<ReviewTheme> Themes =
    [
        new("light", UiThemeMode.Light),
        new("dark", UiThemeMode.Dark)
    ];

    private static readonly ReviewTheme HighContrastTheme =
        new("high-contrast", UiThemeMode.System);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task RunAsync(ReviewCommandLine options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var application = Avalonia.Application.Current as ReviewApplication
            ?? throw new InvalidOperationException("ReviewApplication was not initialized.");
        var timeProvider = new FixedTimeProvider(CanonicalScenarioCatalog.ClockUtc);
        var shell = new ReviewDesktopShell();
        await ValidateLiveLanguageSwitchAsync(application, timeProvider, shell);

        PrepareOutput(options.OutputRoot);
        var artifacts = new List<ReviewArtifact>();
        var contrastChecks = new List<TokenContrastCheck>();

        foreach (var language in Languages)
        {
            application.Localization.SetLanguage(language.Value);
            foreach (var theme in Themes)
            {
                application.Themes.SetTheme(theme.Mode);
                if (language.Id == Languages[0].Id)
                {
                    contrastChecks.AddRange(TokenContrastValidator.Validate(application, theme.Id));
                }

                foreach (var scale in Scales)
                {
                    foreach (var scenario in CanonicalScenarioCatalog.MainScenarios)
                    {
                        artifacts.Add(await RenderMainWindowAsync(
                            options.OutputRoot,
                            application.Localization,
                            language,
                            theme,
                            scale,
                            scenario,
                            timeProvider,
                            shell));
                    }
                }

                artifacts.AddRange(await RenderAuxiliarySurfacesAsync(
                    options.OutputRoot,
                    application,
                    language,
                    theme,
                    timeProvider,
                    shell));
                artifacts.AddRange(await RenderEdgeCaseSurfacesAsync(
                    options.OutputRoot,
                    application,
                    language,
                    theme,
                    timeProvider,
                    shell,
                    includeSetupAndSettings: true));
            }

            application.RequestedThemeVariant = CalmInstrumentThemeVariants.HighContrast;
            if (language.Id == Languages[0].Id)
            {
                contrastChecks.AddRange(TokenContrastValidator.Validate(application, HighContrastTheme.Id));
            }

            foreach (var scenario in CanonicalScenarioCatalog.MainScenarios)
            {
                artifacts.Add(await RenderMainWindowAsync(
                    options.OutputRoot,
                    application.Localization,
                    language,
                    HighContrastTheme,
                    Scales[0],
                    scenario,
                    timeProvider,
                    shell));
            }

            artifacts.AddRange(await RenderAuxiliarySurfacesAsync(
                options.OutputRoot,
                application,
                language,
                HighContrastTheme,
                timeProvider,
                shell));
            artifacts.AddRange(await RenderEdgeCaseSurfacesAsync(
                options.OutputRoot,
                application,
                language,
                HighContrastTheme,
                timeProvider,
                shell,
                includeSetupAndSettings: false));
        }

        artifacts.AddRange(ContactSheetWriter.Create(options.OutputRoot, artifacts));
        VisualArtifactValidator.Validate(options.OutputRoot, artifacts, contrastChecks);
        WriteManifest(options.OutputRoot, artifacts, contrastChecks);
        WriteReadme(options.OutputRoot, artifacts, contrastChecks);
        application.Localization.SetLanguage(UiLanguage.Russian);

        var renderedScreens = artifacts
            .Where(static item => item.Kind == ReviewArtifactKind.Screen)
            .ToArray();
        var renderedCanonical = renderedScreens.Count(static item => item.Suite == ReviewArtifactSuite.Canonical);
        var renderedEdgeCases = renderedScreens.Count(static item => item.Suite == ReviewArtifactSuite.EdgeCase);
        Console.WriteLine(
            $"Rendered {renderedScreens.Length} screens "
            + $"({renderedCanonical} canonical + {renderedEdgeCases} edge-case).");
        Console.WriteLine($"Created {artifacts.Count(static item => item.Kind == ReviewArtifactKind.ContactSheet)} contact sheets.");
        Console.WriteLine($"Visual review: {options.OutputRoot}");
    }

    /// <summary>
    /// Proves that an already open production window refreshes both DynamicResource values and
    /// computed view-model text when the active language changes.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
    /// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
    /// </remarks>
    private static async Task ValidateLiveLanguageSwitchAsync(
        ReviewApplication application,
        TimeProvider timeProvider,
        ReviewDesktopShell shell)
    {
        application.Themes.SetTheme(UiThemeMode.Light);
        application.Localization.SetLanguage(UiLanguage.Russian);
        var snapshot = CanonicalScenarioCatalog.WithThemeAndLanguage(
            CanonicalScenarioCatalog.MainScenarios[0].Snapshot,
            "light",
            "ru");
        await using var runtime = new ReviewApplicationRuntime(snapshot);
        using var viewModel = new MainWindowViewModel(
            runtime,
            shell,
            application.Localization,
            timeProvider);
        await viewModel.InitializeAsync(CancellationToken.None);
        var window = new MainWindow { DataContext = viewModel };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            var russianWindowTitle = application.Localization.Get("String.Brand.WindowTitle");
            var russianStateTitle = application.Localization.Get("String.State.Listening.Title");
            var russianRecentTitle = application.Localization.Get("String.Recent.Title");
            AssertLocalizedValue("Russian window title", russianWindowTitle, window.Title);
            AssertLocalizedValue("Russian computed state title", russianStateTitle, viewModel.StateTitle);
            AssertVisibleText(window, russianRecentTitle, "Russian DynamicResource text");

            application.Localization.SetLanguage(UiLanguage.English);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            var englishWindowTitle = application.Localization.Get("String.Brand.WindowTitle");
            var englishStateTitle = application.Localization.Get("String.State.Listening.Title");
            var englishRecentTitle = application.Localization.Get("String.Recent.Title");
            AssertLocalizedValue("English window title", englishWindowTitle, window.Title, russianWindowTitle);
            AssertLocalizedValue(
                "English computed state title",
                englishStateTitle,
                viewModel.StateTitle,
                russianStateTitle);
            AssertVisibleText(
                window,
                englishRecentTitle,
                "English DynamicResource text",
                russianRecentTitle);
        }
        finally
        {
            window.Close();
            application.Localization.SetLanguage(UiLanguage.Russian);
        }
    }

    private static void AssertLocalizedValue(
        string role,
        string expected,
        string? actual,
        string? staleValue = null)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)
            || (staleValue is not null && string.Equals(actual, staleValue, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Live language switch failed for {role}: expected '{expected}', found '{actual}'.");
        }
    }

    private static void AssertVisibleText(
        Window window,
        string expected,
        string role,
        string? staleValue = null)
    {
        var visibleTexts = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(static textBlock => textBlock.IsVisible)
            .Select(static textBlock => textBlock.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (!visibleTexts.Contains(expected, StringComparer.Ordinal)
            || (staleValue is not null && visibleTexts.Contains(staleValue, StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                $"Live language switch failed for {role}: expected visible text '{expected}'.");
        }
    }

    private static async Task<ReviewArtifact> RenderMainWindowAsync(
        string outputRoot,
        LocalizationService localization,
        ReviewLanguage language,
        ReviewTheme theme,
        ReviewScale scale,
        CanonicalMainScenario scenario,
        TimeProvider timeProvider,
        ReviewDesktopShell shell)
    {
        var snapshot = CanonicalScenarioCatalog.WithThemeAndLanguage(
            scenario.Snapshot,
            theme.Id,
            language.Id);
        await using var runtime = new ReviewApplicationRuntime(snapshot);
        using var viewModel = new MainWindowViewModel(runtime, shell, localization, timeProvider);
        await viewModel.InitializeAsync(CancellationToken.None);
        var window = new MainWindow { DataContext = viewModel };
        var relativePath = Path.Combine(
            "languages",
            language.Id,
            "main",
            theme.Id,
            $"{scale.Percent:D3}",
            $"{scenario.Id}.png");

        try
        {
            return Capture(
                outputRoot,
                relativePath,
                window,
                scale,
                surface: "main",
                state: scenario.Id,
                theme.Id,
                language.Id);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<IReadOnlyList<ReviewArtifact>> RenderAuxiliarySurfacesAsync(
        string outputRoot,
        ReviewApplication application,
        ReviewLanguage language,
        ReviewTheme theme,
        TimeProvider timeProvider,
        ReviewDesktopShell shell)
    {
        var artifacts = new List<ReviewArtifact>();
        var scale = Scales[0];
        var settings = CanonicalScenarioCatalog.Settings(
            theme: theme.Id,
            language: language.Id);
        var snapshot = CanonicalScenarioCatalog.WithThemeAndLanguage(
            CanonicalScenarioCatalog.MainScenarios[0].Snapshot,
            theme.Id,
            language.Id) with
        {
            UserSettings = settings,
            AvailableMicrophones = CanonicalScenarioCatalog.Microphones,
            Capability = CanonicalScenarioCatalog.MainScenarios[0].Snapshot.Capability with
            {
                HasActiveOutput = true,
                HasActiveMicrophone = true
            }
        };

        await using var setupRuntime = new ReviewApplicationRuntime(snapshot);
        using (var viewModel = new SetupViewModel(
                   setupRuntime,
                   application.Localization,
                   settings with { OnboardingCompleted = false },
                   CanonicalScenarioCatalog.Microphones))
        {
            var window = new SetupWindow();
            window.Bind(viewModel);
            try
            {
                artifacts.Add(CaptureAuxiliary(
                    outputRoot,
                    window,
                    scale,
                    "setup",
                    theme.Id,
                    language.Id));
            }
            finally
            {
                window.Close();
            }
        }

        await using var settingsRuntime = new ReviewApplicationRuntime(snapshot);
        using (var viewModel = new SettingsViewModel(
                   settingsRuntime,
                   application.Localization,
                   application.Themes,
                   settings,
                   CanonicalScenarioCatalog.Microphones,
                   timeProvider))
        {
            var window = new SettingsWindow();
            window.Bind(viewModel);
            try
            {
                artifacts.Add(CaptureAuxiliary(
                    outputRoot,
                    window,
                    scale,
                    "settings",
                    theme.Id,
                    language.Id));
            }
            finally
            {
                window.CloseForShutdown();
            }
        }

        await using var diagnosticsRuntime = new ReviewApplicationRuntime(snapshot);
        using (var viewModel = new DiagnosticsViewModel(
                   diagnosticsRuntime,
                   application.Localization,
                   shell))
        {
            var window = new DiagnosticsWindow();
            window.Bind(viewModel);
            try
            {
                artifacts.Add(CaptureAuxiliary(
                    outputRoot,
                    window,
                    scale,
                    "diagnostics",
                    theme.Id,
                    language.Id));
            }
            finally
            {
                window.Close();
            }
        }

        await using var askRuntime = new ReviewApplicationRuntime(snapshot);
        using (var viewModel = new AskPromptViewModel(
                   askRuntime,
                   application.Localization,
                   new MeetingPromptSnapshot(
                       "visual-review-ask",
                       "google-meet",
                       "Google Meet · Zen Browser",
                       CanonicalScenarioCatalog.ClockUtc - TimeSpan.FromSeconds(8),
                       CanonicalScenarioCatalog.ClockUtc + TimeSpan.FromSeconds(22),
                       ConfidenceScore: 86,
                       DecisionReasons: ["conversation_detected", "meeting_application_active"]),
                   timeProvider,
                   showAdvancedDiagnostics: false))
        {
            var window = new AskPromptWindow(viewModel);
            try
            {
                artifacts.Add(CaptureAuxiliary(
                    outputRoot,
                    window,
                    scale,
                    "ask",
                    theme.Id,
                    language.Id));
            }
            finally
            {
                window.CloseWithoutResolution();
            }
        }

        using var confirmationViewModel = new ConfirmationDialogViewModel(
            application.Localization,
            "String.Confirmation.RemoveHistory.Title",
            "String.Confirmation.RemoveHistory.Description",
            "String.Confirmation.RemoveHistory.KeepFile",
            "String.Action.Cancel",
            "String.Confirmation.RemoveHistory.DeleteFile");
        var confirmationWindow = new ConfirmationDialog { DataContext = confirmationViewModel };
        try
        {
            artifacts.Add(CaptureAuxiliary(
                outputRoot,
                confirmationWindow,
                scale,
                "confirmation",
                theme.Id,
                language.Id));
        }
        finally
        {
            confirmationWindow.Close();
        }

        return artifacts;
    }

    private static async Task<IReadOnlyList<ReviewArtifact>> RenderEdgeCaseSurfacesAsync(
        string outputRoot,
        ReviewApplication application,
        ReviewLanguage language,
        ReviewTheme theme,
        TimeProvider timeProvider,
        ReviewDesktopShell shell,
        bool includeSetupAndSettings)
    {
        var artifacts = new List<ReviewArtifact>();
        var scale = Scales[0];
        var recordingSnapshot = EdgeCaseScenarioCatalog.RecordingWithoutMicrophone(
            theme.Id,
            language.Id);
        await using (var runtime = new ReviewApplicationRuntime(recordingSnapshot))
        using (var viewModel = new MainWindowViewModel(
                   runtime,
                   shell,
                   application.Localization,
                   timeProvider))
        {
            await viewModel.InitializeAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                artifacts.Add(CaptureEdgeCase(
                    outputRoot,
                    window,
                    scale,
                    surface: "main",
                    state: "recording-microphone-unavailable",
                    theme: theme.Id,
                    language: language.Id));
            }
            finally
            {
                window.Close();
            }
        }

        // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
        var legacyTranscriptSnapshot = EdgeCaseScenarioCatalog.LegacyTranscriptArtifact(
            theme.Id,
            language.Id);
        await using (var runtime = new ReviewApplicationRuntime(legacyTranscriptSnapshot))
        using (var viewModel = new MainWindowViewModel(
                   runtime,
                   shell,
                   application.Localization,
                   timeProvider))
        {
            await viewModel.InitializeAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                artifacts.Add(CaptureEdgeCase(
                    outputRoot,
                    window,
                    scale,
                    surface: "main",
                    state: "legacy-transcript-action",
                    theme: theme.Id,
                    language: language.Id));
            }
            finally
            {
                window.Close();
            }
        }

        if (!includeSetupAndSettings)
        {
            return artifacts;
        }

        var setupSettings = EdgeCaseScenarioCatalog.SelectedMicrophoneUnavailableSettings(
            theme.Id,
            language.Id,
            onboardingCompleted: false);
        var setupSnapshot = EdgeCaseScenarioCatalog.SelectedMicrophoneUnavailable(
            theme.Id,
            language.Id,
            onboardingCompleted: false,
            issue: RuntimeCapabilityIssue.MicrophoneCaptureUnavailable);
        await using (var runtime = new ReviewApplicationRuntime(setupSnapshot))
        using (var viewModel = new SetupViewModel(
                   runtime,
                   application.Localization,
                   setupSettings,
                   CanonicalScenarioCatalog.Microphones))
        {
            await viewModel.CompleteCommand.ExecuteAsync(null);
            var window = new SetupWindow();
            window.Bind(viewModel);
            try
            {
                artifacts.Add(CaptureEdgeCase(
                    outputRoot,
                    window,
                    scale,
                    surface: "setup",
                    state: "selected-microphone-unavailable",
                    theme: theme.Id,
                    language: language.Id,
                    scrollToBottom: true));
            }
            finally
            {
                window.Close();
            }
        }

        var settingsSettings = EdgeCaseScenarioCatalog.SelectedMicrophoneUnavailableSettings(
            theme.Id,
            language.Id,
            onboardingCompleted: true);
        var settingsSnapshot = EdgeCaseScenarioCatalog.SelectedMicrophoneUnavailable(
            theme.Id,
            language.Id,
            onboardingCompleted: true);
        await using (var runtime = new ReviewApplicationRuntime(settingsSnapshot))
        using (var viewModel = new SettingsViewModel(
                   runtime,
                   application.Localization,
                   application.Themes,
                   settingsSettings,
                   CanonicalScenarioCatalog.Microphones,
                   timeProvider))
        {
            var window = new SettingsWindow();
            window.Bind(viewModel);
            try
            {
                artifacts.Add(CaptureEdgeCase(
                    outputRoot,
                    window,
                    scale,
                    surface: "settings",
                    state: "selected-microphone-unavailable",
                    theme: theme.Id,
                    language: language.Id,
                    scrollToBottom: true));
            }
            finally
            {
                window.CloseForShutdown();
            }
        }

        return artifacts;
    }

    private static ReviewArtifact CaptureAuxiliary(
        string outputRoot,
        Window window,
        ReviewScale scale,
        string surface,
        string theme,
        string language) => Capture(
            outputRoot,
            Path.Combine("languages", language, "auxiliary", theme, $"{surface}.png"),
            window,
            scale,
            surface,
            state: null,
            theme,
            language);

    private static ReviewArtifact CaptureEdgeCase(
        string outputRoot,
        Window window,
        ReviewScale scale,
        string surface,
        string state,
        string theme,
        string language,
        bool scrollToBottom = false) => Capture(
            outputRoot,
            Path.Combine(
                "languages",
                language,
                "edge-cases",
                theme,
                $"{surface}-{state}.png"),
            window,
            scale,
            surface,
            state,
            theme,
            language,
            ReviewArtifactSuite.EdgeCase,
            scrollToBottom ? ScrollPrimaryViewportToBottom : null);

    private static ReviewArtifact Capture(
        string outputRoot,
        string relativePath,
        Window window,
        ReviewScale scale,
        string surface,
        string? state,
        string theme,
        string language,
        ReviewArtifactSuite suite = ReviewArtifactSuite.Canonical,
        Action<Window>? prepareCapture = null)
    {
        window.Show();
        window.SetRenderScaling(scale.Factor);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        if (prepareCapture is not null)
        {
            prepareCapture(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        }

        using var bitmap = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException($"'{surface}' did not produce a rendered frame.");
        var absolutePath = Path.Combine(outputRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        bitmap.Save(absolutePath, new PngBitmapEncoderOptions());

        return ReviewArtifact.Create(
            ReviewArtifactKind.Screen,
            surface,
            state,
            theme,
            language,
            scale.Percent,
            window.Bounds.Width,
            window.Bounds.Height,
            bitmap.PixelSize.Width,
            bitmap.PixelSize.Height,
            NormalizeRelativePath(relativePath),
            absolutePath,
            suite);
    }

    private static void ScrollPrimaryViewportToBottom(Window window)
    {
        var scrollViewer = window
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .OrderByDescending(static viewer => viewer.Extent.Height)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"'{window.GetType().Name}' has no rendered ScrollViewer for edge-case review.");
        scrollViewer.Offset = new Vector(
            scrollViewer.Offset.X,
            Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height));
    }

    private static void PrepareOutput(string outputRoot)
    {
        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        foreach (var directoryName in new[]
                 {
                     "languages",
                     "main",
                     "auxiliary",
                     "edge-cases",
                     "contact-sheets"
                 })
        {
            var directory = Path.GetFullPath(Path.Combine(root, directoryName));
            if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Artifact cleanup escaped the requested output root.");
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        foreach (var fileName in new[] { "manifest.json", "README.md" })
        {
            var file = Path.Combine(root, fileName);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static void WriteManifest(
        string outputRoot,
        IReadOnlyList<ReviewArtifact> artifacts,
        IReadOnlyList<TokenContrastCheck> contrastChecks)
    {
        var manifest = new ReviewManifest(
            SchemaVersion: 3,
            Spec: "spec://modules/app/FEAT-010.A-release-v2-localization#verification",
            Languages: Languages.Select(language => new ReviewLanguageManifest(
                language.Id,
                language.CultureName,
                ScreenCount: artifacts.Count(artifact =>
                    artifact.Kind == ReviewArtifactKind.Screen
                    && artifact.Language == language.Id),
                CanonicalScreenCount: artifacts.Count(artifact =>
                    artifact.Kind == ReviewArtifactKind.Screen
                    && artifact.Suite == ReviewArtifactSuite.Canonical
                    && artifact.Language == language.Id),
                EdgeCaseScreenCount: artifacts.Count(artifact =>
                    artifact.Kind == ReviewArtifactKind.Screen
                    && artifact.Suite == ReviewArtifactSuite.EdgeCase
                    && artifact.Language == language.Id)))
                .ToArray(),
            LiveLanguageSwitchValidated: true,
            CanonicalClockUtc: CanonicalScenarioCatalog.ClockUtc,
            RenderPlatform: "windows",
            AvaloniaVersion: "12.1.0",
            MainStateCount: CanonicalScenarioCatalog.MainScenarios.Count,
            CanonicalScreenCount: artifacts.Count(static artifact =>
                artifact.Kind == ReviewArtifactKind.Screen
                && artifact.Suite == ReviewArtifactSuite.Canonical),
            EdgeCaseScreenCount: artifacts.Count(static artifact =>
                artifact.Kind == ReviewArtifactKind.Screen
                && artifact.Suite == ReviewArtifactSuite.EdgeCase),
            MainScalePercents: Scales.Select(static scale => scale.Percent).ToArray(),
            Themes: Themes
                .Select(static theme => theme.Id)
                .Append(HighContrastTheme.Id)
                .ToArray(),
            ContrastChecks: contrastChecks,
            Artifacts: artifacts.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToArray());
        File.WriteAllText(
            Path.Combine(outputRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteReadme(
        string outputRoot,
        IReadOnlyList<ReviewArtifact> artifacts,
        IReadOnlyList<TokenContrastCheck> contrastChecks)
    {
        var screenCount = artifacts.Count(static item => item.Kind == ReviewArtifactKind.Screen);
        var canonicalScreenCount = artifacts.Count(static item =>
            item.Kind == ReviewArtifactKind.Screen
            && item.Suite == ReviewArtifactSuite.Canonical);
        var edgeCaseScreenCount = artifacts.Count(static item =>
            item.Kind == ReviewArtifactKind.Screen
            && item.Suite == ReviewArtifactSuite.EdgeCase);
        var contactSheets = artifacts
            .Where(static item => item.Kind == ReviewArtifactKind.ContactSheet)
            .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# FEAT-010.A bilingual visual review");
        builder.AppendLine();
        builder.AppendLine("Deterministic render output from the real Avalonia desktop windows, view models, localization dictionaries and Calm Instrument theme resources.");
        builder.AppendLine();
        builder.AppendLine($"- Screens: {screenCount}");
        builder.AppendLine($"- Canonical screens: {canonicalScreenCount}");
        builder.AppendLine($"- Edge-case screens: {edgeCaseScreenCount}");
        builder.AppendLine("- Languages: ru-RU and en-US");
        builder.AppendLine("- Main matrix per language: 8 canonical states × light/dark × 100/150/200% (48 PNG)");
        builder.AppendLine("- Auxiliary matrix per language: setup, settings, diagnostics, Ask and confirmation × light/dark (10 PNG)");
        builder.AppendLine("- High-contrast matrix per language: 8 canonical states + setup, settings, diagnostics, Ask and confirmation at 100% (13 PNG)");
        builder.AppendLine("- Live language switch: open Main window RU → EN, including title, DynamicResource and computed view-model text");
        builder.AppendLine("- Render platform: Windows");
        builder.AppendLine($"- Canonical clock: {CanonicalScenarioCatalog.ClockUtc:O}");
        builder.AppendLine("- Manifest: `manifest.json` (logical and physical sizes plus SHA-256)");
        builder.AppendLine();
        builder.AppendLine("## Language matrix");
        builder.AppendLine();
        builder.AppendLine("| Language | Screens | Canonical | Edge cases |");
        builder.AppendLine("|---|---:|---:|---:|");
        foreach (var language in Languages)
        {
            var languageScreens = artifacts.Where(artifact =>
                artifact.Kind == ReviewArtifactKind.Screen
                && artifact.Language == language.Id).ToArray();
            builder.AppendLine(
                $"| {language.CultureName} | {languageScreens.Length} | "
                + $"{languageScreens.Count(static artifact => artifact.Suite == ReviewArtifactSuite.Canonical)} | "
                + $"{languageScreens.Count(static artifact => artifact.Suite == ReviewArtifactSuite.EdgeCase)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Canonical states");
        builder.AppendLine();
        foreach (var scenario in CanonicalScenarioCatalog.MainScenarios)
        {
            builder.AppendLine($"- `{scenario.Id}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Bounded edge cases");
        builder.AppendLine();
        builder.AppendLine("- Active recording with meeting audio continuing, microphone capture unavailable, inline warning and retry in light, dark and high contrast.");
        builder.AppendLine("- Setup with an unavailable explicitly selected microphone, microphone-capture notice and acknowledged `Continue without microphone` action in light and dark.");
        builder.AppendLine("- Settings with an unavailable explicitly selected microphone in light and dark.");
        builder.AppendLine("- A preserved legacy transcript action beside its local recording in light, dark and high contrast.");

        builder.AppendLine();
        builder.AppendLine("## Contrast acceptance");
        builder.AppendLine();
        builder.AppendLine("Every primary, secondary and tertiary text token is checked against the window and all surface tokens. On-accent text is checked against the primary accent. Each pair must meet 4.5:1.");
        builder.AppendLine();
        builder.AppendLine("| Theme | Checks | Lowest ratio | Result |");
        builder.AppendLine("|---|---:|---:|---|");
        foreach (var theme in Themes.Select(static item => item.Id).Append(HighContrastTheme.Id))
        {
            var themeChecks = contrastChecks.Where(check => check.Theme == theme).ToArray();
            var minimumRatio = themeChecks
                .Min(static check => check.Ratio)
                .ToString("0.00", CultureInfo.InvariantCulture);
            builder.AppendLine($"| {theme} | {themeChecks.Length} | {minimumRatio}:1 | pass |");
        }

        builder.AppendLine();
        builder.AppendLine("## Reduced motion");
        builder.AppendLine();
        builder.AppendLine("The production FEAT-013 surfaces declare no decorative transitions or indeterminate animations. Processing uses determinate progress and the Ask countdown is functional state, so reduced-motion mode has no decorative motion to suppress.");

        builder.AppendLine();
        builder.AppendLine("## Contact sheets");
        builder.AppendLine();
        foreach (var artifact in contactSheets)
        {
            builder.AppendLine($"- `{artifact.RelativePath}` ({artifact.PixelWidth}×{artifact.PixelHeight})");
        }

        builder.AppendLine();
        builder.AppendLine("## Reproduce on Windows");
        builder.AppendLine();
        builder.AppendLine("```powershell");
        builder.AppendLine("dotnet run --project tools/IsTranscribe.VisualReview/IsTranscribe.VisualReview.csproj -c Release");
        builder.AppendLine("```");
        File.WriteAllText(
            Path.Combine(outputRoot, "README.md"),
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');
}

internal sealed record ReviewScale(int Percent, double Factor);

internal sealed record ReviewTheme(string Id, UiThemeMode Mode);

internal sealed record ReviewLanguage(string Id, string CultureName, UiLanguage Value);

internal enum ReviewArtifactKind
{
    Screen,
    ContactSheet
}

internal enum ReviewArtifactSuite
{
    Canonical,
    EdgeCase
}

internal sealed record ReviewArtifact(
    ReviewArtifactKind Kind,
    string Surface,
    string? State,
    string Theme,
    string Language,
    int ScalePercent,
    double LogicalWidth,
    double LogicalHeight,
    int PixelWidth,
    int PixelHeight,
    string RelativePath,
    string Sha256)
{
    public ReviewArtifactSuite Suite { get; init; } = ReviewArtifactSuite.Canonical;

    public static ReviewArtifact Create(
        ReviewArtifactKind kind,
        string surface,
        string? state,
        string theme,
        string language,
        int scalePercent,
        double logicalWidth,
        double logicalHeight,
        int pixelWidth,
        int pixelHeight,
        string relativePath,
        string absolutePath,
        ReviewArtifactSuite suite = ReviewArtifactSuite.Canonical) => new(
            kind,
            surface,
            state,
            theme,
            language,
            scalePercent,
            Math.Round(logicalWidth, 2),
            Math.Round(logicalHeight, 2),
            pixelWidth,
            pixelHeight,
            relativePath,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath))).ToLowerInvariant())
        {
            Suite = suite
        };
}

internal sealed record ReviewManifest(
    int SchemaVersion,
    string Spec,
    IReadOnlyList<ReviewLanguageManifest> Languages,
    bool LiveLanguageSwitchValidated,
    DateTimeOffset CanonicalClockUtc,
    string RenderPlatform,
    string AvaloniaVersion,
    int MainStateCount,
    int CanonicalScreenCount,
    int EdgeCaseScreenCount,
    IReadOnlyList<int> MainScalePercents,
    IReadOnlyList<string> Themes,
    IReadOnlyList<TokenContrastCheck> ContrastChecks,
    IReadOnlyList<ReviewArtifact> Artifacts);

internal sealed record ReviewLanguageManifest(
    string Id,
    string Culture,
    int ScreenCount,
    int CanonicalScreenCount,
    int EdgeCaseScreenCount);

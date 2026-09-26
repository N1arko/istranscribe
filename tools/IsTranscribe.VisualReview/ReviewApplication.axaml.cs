using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Platform;
using IsTranscribe.Desktop;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Theming;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Minimal application host that loads the production localization and Calm Instrument resources.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// </remarks>
internal sealed partial class ReviewApplication : Avalonia.Application
{
    public LocalizationService Localization { get; } = new(UiLanguage.Russian);

    public CalmInstrumentThemeService Themes { get; } = new(UiThemeMode.Light);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Localization.Attach(this);
        Themes.Attach(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DesktopComposition.Configure(new DesktopComposition(
            CreateRuntime: static _ => throw new InvalidOperationException(
                "Visual review supplies canonical runtimes per surface."),
            CreateInstanceCoordinator: static () => new ReviewApplicationInstanceCoordinator(),
            PlatformDescriptor: new ReviewPlatformDescriptor(),
            Shell: new ReviewDesktopShell(),
            ActiveWorkAreaProvider: new ReviewActiveWorkAreaProvider()));
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            throw new InvalidOperationException("Visual review requires a classic desktop lifetime.");
        }

        Dispatcher.UIThread.Post(() => _ = RenderAndShutdownAsync(desktop));
    }

    private static async Task RenderAndShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var exitCode = 0;
        try
        {
            if (Program.Options.IsStoreListingCapture)
            {
                await StoreListingScreenshotRunner.RunAsync(Program.Options.StoreListingOutputRoot!);
            }
            else
            {
                await VisualReviewRunner.RunAsync(Program.Options);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            exitCode = 1;
        }
        finally
        {
            desktop.Shutdown(exitCode);
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Headless;
using Avalonia.Skia;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Deterministic bilingual release-surface visual review entry point.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// </remarks>
internal static class Program
{
    public static ReviewCommandLine Options { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The release visual review is pinned to the Windows rendering stack.");
            }

            Options = ReviewCommandLine.Parse(args);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                [],
                ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ReviewApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                ShouldRenderOnUIThread = true,
                UseHeadlessDrawing = false
            })
            .UseSkia()
            .WithInterFont();
}

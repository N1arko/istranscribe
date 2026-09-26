using Avalonia;
using IsTranscribe.Application;
using IsTranscribe.Desktop;
using IsTranscribe.Platform.MacOS;
using IsTranscribe.Transcription.Local.Models;
using AvaloniaApplication = Avalonia.Application;
using DesktopApp = IsTranscribe.Desktop.App;

namespace IsTranscribe.App.MacOS;

/// <summary>
/// Production macOS composition boundary for the shared runtime and desktop shell.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#entrypoints
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#parity
/// </remarks>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var platform = new MacOSApplicationPlatformRuntimeAdapter();

        // @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
        // @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.macos
        var modelStoreRoot = WhisperModelStorageLayout.GetDefaultRootPath();
        var localTranscriptionOptions = MacOSLocalTranscriptionComposition.Create(
            AppContext.BaseDirectory,
            modelStoreRoot);
        var askPromptPresenter = new MacOSAskPromptWindowPresenter();
        DesktopComposition.Configure(new DesktopComposition(
            CreateRuntime: _ => new ApplicationRuntime(
                platform,
                localTranscriptionOptions: localTranscriptionOptions),
            CreateInstanceCoordinator: static () => new MacOSApplicationInstanceCoordinator(),
            PlatformDescriptor: new MacOSPlatformDescriptor(),
            Shell: new MacOSPlatformShell(),
            ActiveWorkAreaProvider: new MacOSActiveWorkAreaProvider(),
            SystemNotifications: new MacOSSystemNotificationService(),
            Permissions: new MacOSPermissionService(),
            PresentAskPromptWindow: askPromptPresenter.Present));

        var coordinatorLease = new EarlyApplicationInstanceBootstrap(
                new MacOSApplicationInstanceCoordinator())
            .AcquirePrimaryAsync(CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (coordinatorLease is null)
        {
            return;
        }

        try
        {
            DesktopApp.SetPreRegisteredInstanceCoordinator(coordinatorLease);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch
        {
            if (AvaloniaApplication.Current is DesktopApp app)
            {
                app.DisposeInstanceCoordinatorAfterStartupFailure();
            }
            throw;
        }
        finally
        {
            DesktopApp.ClearPreRegisteredInstanceCoordinator(coordinatorLease);
            coordinatorLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DesktopApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

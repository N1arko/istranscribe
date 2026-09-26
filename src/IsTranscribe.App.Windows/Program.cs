using Avalonia;
using System.Runtime.Versioning;
using IsTranscribe.Application;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop;
using IsTranscribe.Platform.Windows;
using IsTranscribe.Transcription.Local.Models;
using AvaloniaApplication = Avalonia.Application;
using DesktopApp = IsTranscribe.Desktop.App;

namespace IsTranscribe.App.Windows;

/// <summary>
/// Release-v2 Avalonia entrypoint.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
    public static void Main(string[] args)
    {
        var platformRuntime = new WindowsApplicationPlatformRuntimeAdapter();

        // @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
        // @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.windows
        var modelStoreRoot = WhisperModelStorageLayout.GetDefaultRootPath();
        var localTranscriptionOptions = WindowsLocalTranscriptionComposition.Create(
            AppContext.BaseDirectory,
            modelStoreRoot);
        DesktopComposition.Configure(new DesktopComposition(
            CreateRuntime: runtimeArgs =>
            {
                var detectionMode = WindowsDetectionModeConfiguration.Resolve(
                    runtimeArgs,
                    Environment.GetEnvironmentVariable(
                        WindowsDetectionModeConfiguration.EnvironmentVariableName));
                return new ApplicationRuntime(
                    platformRuntime,
                    detectionMode: detectionMode,
                    localTranscriptionOptions: localTranscriptionOptions);
            },
            CreateInstanceCoordinator: static () => new WindowsApplicationInstanceCoordinator(),
            PlatformDescriptor: new WindowsPlatformDescriptor(),
            Shell: new WindowsPlatformShell(),
            ActiveWorkAreaProvider: new WindowsActiveWorkAreaProvider()));

        var coordinatorLease = new EarlyApplicationInstanceBootstrap(
                new WindowsApplicationInstanceCoordinator())
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

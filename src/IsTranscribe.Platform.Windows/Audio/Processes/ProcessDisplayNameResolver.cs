using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace IsTranscribe.Host.Audio.Processes;

[SupportedOSPlatform("windows")]
public static class ProcessDisplayNameResolver
{
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.known-app
    public static string Resolve(int processId, string fallbackProcessName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Resolve(
                fallbackProcessName,
                GetFileDescription(process),
                process.MainWindowTitle);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return Resolve(fallbackProcessName, fileDescription: null, mainWindowTitle: null);
    }

    internal static string Resolve(string fallbackProcessName, string? fileDescription, string? mainWindowTitle)
    {
        if (!string.IsNullOrWhiteSpace(fileDescription))
        {
            return fileDescription.Trim();
        }

        var fallbackName = Path.GetFileNameWithoutExtension(fallbackProcessName)?.Trim();
        if (!string.IsNullOrWhiteSpace(fallbackName))
        {
            return fallbackName;
        }

        if (!string.IsNullOrWhiteSpace(mainWindowTitle))
        {
            return mainWindowTitle.Trim();
        }

        return "Unknown app";
    }

    private static string? GetFileDescription(Process process)
    {
        try
        {
            return process.MainModule?.FileVersionInfo?.FileDescription;
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }

        return null;
    }
}

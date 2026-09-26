namespace IsTranscribe.Transcription.Local.Models;

/// <summary>
/// Resolves the persistent app-owned model cache outside the installation directory.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#models</remarks>
public static class WhisperModelStorageLayout
{
    public static string GetDefaultRootPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                RequireFolder(Environment.SpecialFolder.LocalApplicationData),
                "isTranscribe",
                "models");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                RequireFolder(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "isTranscribe",
                "models");
        }

        throw new PlatformNotSupportedException(
            "The initial local transcription model store supports Windows and macOS.");
    }

    private static string RequireFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException($"The OS folder '{folder}' could not be resolved.")
            : path;
    }
}

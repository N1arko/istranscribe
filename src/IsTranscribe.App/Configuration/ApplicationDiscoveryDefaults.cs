namespace IsTranscribe.App.Configuration;

public static class ApplicationDiscoveryDefaults
{
    public static readonly string[] BuiltInExclusions =
    [
        "System",
        "explorer.exe",
        "SearchHost.exe",
        "RuntimeBroker.exe",
        "ShellExperienceHost.exe"
    ];
}

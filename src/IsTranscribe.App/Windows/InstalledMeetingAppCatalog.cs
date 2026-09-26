using System.IO;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.App.Windows;

internal static class InstalledMeetingAppCatalog
{
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
    // @spec spec://modules/app/FEAT-005-application-rules-and-discovery#behavior
    public static IReadOnlyList<AppRuleRecord> GetInstalledSuggestions()
    {
        var suggestions = new List<AppRuleRecord>();

        AddIfInstalled(
            suggestions,
            "Zoom",
            "Zoom.exe",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zoom", "bin", "Zoom.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zoom", "bin", "Zoom.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Zoom", "bin", "Zoom.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zoom", "bin", "Zoom.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zoom", "bin", "Zoom.exe")
            ]);

        return suggestions
            .Select(AppRuleRecord.Normalize)
            .GroupBy(static rule => rule.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    private static void AddIfInstalled(List<AppRuleRecord> rules, string displayName, string processName, IEnumerable<string> probePaths)
    {
        foreach (var path in probePaths.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            rules.Add(AppRuleRecord.Create(displayName, processName));
            return;
        }
    }
}

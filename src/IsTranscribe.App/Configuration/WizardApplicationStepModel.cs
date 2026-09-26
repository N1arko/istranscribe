using IsTranscribe.App.Strings;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.App.Configuration;

internal static class WizardApplicationStepModel
{
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
    public static IReadOnlyList<AppRuleRecord> CreateInitialSelectedApplications(
        IEnumerable<AppRuleRecord> existingRules,
        IEnumerable<AppRuleRecord> installedSuggestions)
    {
        return installedSuggestions
            .Concat(existingRules)
            .Select(AppRuleRecord.Normalize)
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.ProcessName))
            .GroupBy(static rule => rule.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
    public static string BuildWhitelistWarning(string recordingMode, int selectedCount)
    {
        if (selectedCount > 0)
        {
            return string.Empty;
        }

        return string.Equals(recordingMode, "ask", StringComparison.OrdinalIgnoreCase)
            || string.Equals(recordingMode, "auto", StringComparison.OrdinalIgnoreCase)
            ? LocalizationManager.Instance["Wizard_Apps_WhitelistWarning"]
            : string.Empty;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
    public static string BuildSelectionSummary(int selectedCount) =>
        selectedCount == 0
            ? LocalizationManager.Instance["Wizard_Apps_NoAppsSelected"]
            : string.Format(LocalizationManager.Instance["Wizard_Apps_SelectionSummary"], selectedCount);
}

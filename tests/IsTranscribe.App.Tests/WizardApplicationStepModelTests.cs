using IsTranscribe.App.Configuration;
using IsTranscribe.App.Strings;
using IsTranscribe.Host.Persistence;
using Xunit;

namespace IsTranscribe.App.Tests;

public sealed class WizardApplicationStepModelTests
{
    public WizardApplicationStepModelTests()
    {
        LocalizationManager.Instance.SwitchLanguage("en");
    }

    [Fact]
    public void InitialSelectionsPrefillInstalledSuggestionsAndKeepManualRules()
    {
        var installed = new[]
        {
            AppRuleRecord.Create("Zoom", "zoom.exe")
        };
        var existing = new[]
        {
            AppRuleRecord.Create("Custom Meet", "custommeet.exe"),
            AppRuleRecord.Create("Zoom", "Zoom.exe")
        };

        var selected = WizardApplicationStepModel.CreateInitialSelectedApplications(existing, installed);

        Assert.Equal(["zoom.exe", "custommeet.exe"], selected.Select(static rule => rule.ProcessName).ToArray());
    }

    [Theory]
    [InlineData("off", 0, "")]
    [InlineData("ask", 0, "Without apps in the list automatic detection will not work. You can add applications later in Settings.")]
    [InlineData("auto", 0, "Without apps in the list automatic detection will not work. You can add applications later in Settings.")]
    [InlineData("ask", 1, "")]
    public void EmptyWhitelistWarningMatchesRecordingMode(string mode, int selectedCount, string expected)
    {
        var warning = WizardApplicationStepModel.BuildWhitelistWarning(mode, selectedCount);

        Assert.Equal(expected, warning);
    }
}

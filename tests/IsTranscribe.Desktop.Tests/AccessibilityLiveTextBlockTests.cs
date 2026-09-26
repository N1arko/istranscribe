using Avalonia.Automation;
using IsTranscribe.Desktop.Views;

namespace IsTranscribe.Desktop.Tests;

public sealed class AccessibilityLiveTextBlockTests
{
    /// <summary>
    /// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
    /// </summary>
    [Fact]
    public void Live_status_blocks_avoid_the_unsafe_macos_native_announcement()
    {
        var polite = new PoliteLiveTextBlock();
        var assertive = new AssertiveLiveTextBlock();

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(polite));
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(assertive));
            return;
        }

        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(polite));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(assertive));
    }
}

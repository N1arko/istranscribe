using IsTranscribe.DetectionAcceptance;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#validation-levels
/// </summary>
public sealed class AcceptanceOptionsTests
{
    [Fact]
    public void ParseAcceptsExplicitZenBrowserProcess()
    {
        var options = AcceptanceOptions.Parse(
        [
            "--profile", "google-meet",
            "--surface", "browser",
            "--client-process", "ZEN"
        ]);

        Assert.Equal("zen.exe", options.PreferredClientProcessName);
    }

    [Fact]
    public void ParseRejectsClientOutsideSelectedProfileSurface()
    {
        var exception = Assert.Throws<ArgumentException>(() => AcceptanceOptions.Parse(
        [
            "--profile", "google-meet",
            "--surface", "browser",
            "--client-process", "notepad.exe"
        ]));

        Assert.Contains("selected profile and surface", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsClientPath()
    {
        var exception = Assert.Throws<ArgumentException>(() => AcceptanceOptions.Parse(
        [
            "--profile", "google-meet",
            "--surface", "browser",
            "--client-process", @"C:\Program Files\Zen Browser\zen.exe"
        ]));

        Assert.Contains("executable name", exception.Message, StringComparison.Ordinal);
    }
}

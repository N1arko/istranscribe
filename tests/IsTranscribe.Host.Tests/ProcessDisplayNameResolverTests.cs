using System.Runtime.Versioning;
using IsTranscribe.Host.Audio.Processes;
using Xunit;

namespace IsTranscribe.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProcessDisplayNameResolverTests
{
    [Fact]
    public void ResolvePrefersStableFileDescriptionOverWindowTitle()
    {
        var result = ProcessDisplayNameResolver.Resolve(
            "zen.exe",
            fileDescription: "Zen Browser",
            mainWindowTitle: "Yandex Music - tab title");

        Assert.Equal("Zen Browser", result);
    }

    [Fact]
    public void ResolveFallsBackToProcessNameBeforeWindowTitle()
    {
        var result = ProcessDisplayNameResolver.Resolve(
            "zen.exe",
            fileDescription: null,
            mainWindowTitle: "Some active tab");

        Assert.Equal("zen", result);
    }
}

using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Runtime;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// </summary>
public sealed class RecentRecordingRenameContractTests
{
    [Fact]
    public void RuntimeBoundaryExposesRecentRecordingRename()
    {
        var rename = typeof(IApplicationRuntime).GetMethod(
            nameof(IApplicationRuntime.RenameRecentRecordingAsync));

        Assert.NotNull(rename);
        Assert.Equal(typeof(ValueTask), rename.ReturnType);
    }

    [Fact]
    public void SnapshotDisplayTitleFallsBackToSourceLabel()
    {
        var snapshot = new RecentRecordingSnapshot(
            Guid.NewGuid(),
            "Manual recording",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            "meeting.mp3",
            RequiresAttention: false);

        Assert.Equal("Manual recording", snapshot.DisplayTitle);
        Assert.Equal(
            "Planning",
            (snapshot with { DisplayTitle = "Planning" }).DisplayTitle);
    }

    [Theory]
    [InlineData("  Planning  ", "Planning")]
    [InlineData("Обсуждение запуска", "Обсуждение запуска")]
    public void TitleContractNormalizesValidSingleLineValues(string value, string expected)
    {
        Assert.Equal(expected, RecentRecordingTitle.Normalize(value));
    }

    [Fact]
    public void TitleContractRejectsEmptyOverlongAndMultilineValues()
    {
        Assert.Throws<ArgumentException>(() => RecentRecordingTitle.Normalize("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecentRecordingTitle.Normalize(
            new string('x', RecentRecordingTitle.MaxLength + 1)));
        Assert.Throws<ArgumentException>(() => RecentRecordingTitle.Normalize("One\nTwo"));
    }
}

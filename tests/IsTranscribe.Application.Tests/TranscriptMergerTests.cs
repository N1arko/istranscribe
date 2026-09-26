using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// </summary>
public sealed class TranscriptMergerTests
{
    [Fact]
    public void TextOnlyOverlapIsRemovedWithinBoundedWindow()
    {
        var merged = new TranscriptMerger().Merge(
        [
            Completed(0, 0, 10, "one two three four"),
            Completed(1, 8, 18, "three four five six")
        ]);

        Assert.Equal("one two three four five six", merged.Text);
        Assert.Empty(merged.Segments);
    }

    [Fact]
    public void TimestampedSegmentsUseOverlapMidpointAndBecomeAbsolute()
    {
        var first = Completed(
            0,
            0,
            10,
            "before duplicate",
            [
                new TranscriptionSegment("before", TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8)),
                new TranscriptionSegment("duplicate-old", TimeSpan.FromSeconds(9.4), TimeSpan.FromSeconds(9.8))
            ]);
        var second = Completed(
            1,
            8,
            18,
            "duplicate after",
            [
                new TranscriptionSegment("duplicate-new", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.6)),
                new TranscriptionSegment(
                    "after",
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(3),
                    words:
                    [
                        new TranscriptionWord(
                            "after",
                            TimeSpan.FromSeconds(2),
                            TimeSpan.FromSeconds(3))
                    ])
            ]);

        var merged = new TranscriptMerger().Merge([first, second]);

        Assert.Equal(["before", "after"], merged.Segments.Select(static segment => segment.Text));
        Assert.Equal(TimeSpan.FromSeconds(10), merged.Segments[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(11), merged.Segments[1].End);
        Assert.Equal(TimeSpan.FromSeconds(10), merged.Segments[1].Words[0].Start);
        Assert.Equal("before after", merged.Text);
    }

    [Fact]
    public void UsageIsSummedOnlyForCompatibleCurrency()
    {
        var first = Completed(0, 0, 10, "one", usage: new TranscriptionUsage(
            AudioSeconds: 10,
            InputUnits: 2,
            ReportedCost: 0.01m,
            Currency: "USD"));
        var second = Completed(1, 8, 18, "two", usage: new TranscriptionUsage(
            AudioSeconds: 10,
            InputUnits: 3,
            ReportedCost: 0.02m,
            Currency: "USD"));

        var merged = new TranscriptMerger().Merge([first, second]);

        Assert.Equal(20, merged.Usage?.AudioSeconds);
        Assert.Equal(5, merged.Usage?.InputUnits);
        Assert.Equal(0.03m, merged.Usage?.ReportedCost);
        Assert.Equal("USD", merged.Usage?.Currency);
    }

    private static CompletedTranscriptionChunk Completed(
        int index,
        int startSeconds,
        int endSeconds,
        string text,
        IReadOnlyList<TranscriptionSegment>? segments = null,
        TranscriptionUsage? usage = null) =>
        new(
            new AudioChunkDescriptor(
                $"chunk-{index}",
                index,
                TimeSpan.FromSeconds(startSeconds),
                TimeSpan.FromSeconds(endSeconds),
                index == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(2),
                1024),
            TranscriptionResult.Completed(
                text,
                "en",
                segments,
                new TranscriptionResultMetadata(
                    ResolvedModelId: "model",
                    RequestId: $"request-{index}",
                    Usage: usage)));
}

using IsTranscribe.Application.Transcription;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </summary>
public sealed class AudioChunkPlannerTests
{
    private const string SourceHash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    [Fact]
    public void ShortSourceProducesOneStableRange()
    {
        var planner = new AudioChunkPlanner();
        var source = Source(TimeSpan.FromMinutes(3), 6L * 1024 * 1024);

        var first = planner.Plan(source);
        var second = planner.Plan(source);

        var chunk = Assert.Single(first.Chunks);
        Assert.Equal(TimeSpan.Zero, chunk.Start);
        Assert.Equal(source.Duration, chunk.End);
        Assert.Equal(TimeSpan.Zero, chunk.Overlap);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Source, second.Source);
        Assert.Equal(first.Chunks, second.Chunks);
    }

    [Fact]
    public void TwoHourSourceUsesFiveMinuteRangesWithBoundedOverlap()
    {
        var planner = new AudioChunkPlanner();
        var manifest = planner.Plan(Source(TimeSpan.FromHours(2), 120L * 1024 * 1024));

        Assert.Equal(25, manifest.Chunks.Count);
        Assert.All(manifest.Chunks, chunk => Assert.True(chunk.Duration <= TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.Zero, manifest.Chunks[0].Overlap);
        Assert.All(manifest.Chunks.Skip(1), chunk => Assert.Equal(TimeSpan.FromSeconds(2), chunk.Overlap));
        Assert.Equal(TimeSpan.FromHours(2), manifest.Chunks[^1].End);
    }

    [Fact]
    public void SizeBudgetCanReduceRangeBelowDurationBudget()
    {
        var planner = new AudioChunkPlanner();
        var manifest = planner.Plan(Source(TimeSpan.FromMinutes(10), 100L * 1024 * 1024));

        Assert.Equal(6, manifest.Chunks.Count);
        Assert.All(manifest.Chunks, chunk => Assert.True(chunk.EstimatedRawBytes <= 20L * 1024 * 1024));
        Assert.True(manifest.Chunks[0].Duration < TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void ChangedSourceFingerprintChangesEveryChunkIdentity()
    {
        var planner = new AudioChunkPlanner();
        var original = planner.Plan(Source(TimeSpan.FromMinutes(20), 20L * 1024 * 1024));
        var changed = planner.Plan(Source(TimeSpan.FromMinutes(20), 20L * 1024 * 1024) with
        {
            Sha256 = new string('a', 64)
        });

        Assert.Empty(original.Chunks.Select(static chunk => chunk.Id)
            .Intersect(changed.Chunks.Select(static chunk => chunk.Id), StringComparer.Ordinal));
    }

    [Fact]
    public void SplitCreatesTwoStableChildrenAndPreservesCoverage()
    {
        var planner = new AudioChunkPlanner();
        var original = planner.Plan(Source(TimeSpan.FromMinutes(5), 5L * 1024 * 1024));
        var parent = Assert.Single(original.Chunks);

        var split = planner.Split(original, parent.Id);
        var repeated = planner.Split(original, parent.Id);

        Assert.Equal(2, split.Chunks.Count);
        Assert.Equal(split.Version, repeated.Version);
        Assert.Equal(split.Source, repeated.Source);
        Assert.Equal(split.Chunks, repeated.Chunks);
        Assert.All(split.Chunks, child =>
        {
            Assert.Equal(parent.Id, child.ParentChunkId);
            Assert.Equal(1, child.SplitDepth);
            Assert.True(child.Duration >= TimeSpan.FromSeconds(60));
        });
        Assert.Equal(TimeSpan.Zero, split.Chunks[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(5), split.Chunks[1].End);
        Assert.Equal(TimeSpan.FromSeconds(2), split.Chunks[1].Overlap);
    }

    [Fact]
    public void SplitStopsBeforeEitherChildWouldFallBelowOneMinute()
    {
        var planner = new AudioChunkPlanner();
        var manifest = planner.Plan(Source(TimeSpan.FromSeconds(119), 1024 * 1024));

        Assert.Throws<InvalidOperationException>(() =>
            planner.Split(manifest, Assert.Single(manifest.Chunks).Id));
    }

    private static AudioSourceFingerprint Source(TimeSpan duration, long sizeBytes) =>
        new(SourceHash, sizeBytes, duration, "mp3");
}

using System.Text.Json;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Protocol;
using Xunit;

namespace IsTranscribe.Application.Tests;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#verification
public sealed class SpeakerAwareTranscriptionTests
{
    [Theory]
    [InlineData(false, "remote.groq", "off")]
    [InlineData(false, "local.whisper", "off")]
    [InlineData(true, "local.whisper", "local")]
    [InlineData(true, "remote.groq", "online")]
    [InlineData(true, "remote.openrouter", "online")]
    [InlineData(true, "invalid", "off")]
    public void ModesMigrateWithoutNewOnlineOptIn(bool enabled, string engine, string expected)
    {
        var settings = new TranscriptionPreferences(engine, enabled, "ru", [], true).Canonicalize();
        Assert.Equal(expected, settings.Mode);
        Assert.Equal("auto", settings.Language);
    }

    [Fact]
    public void MicrophoneIdentityIsSourceKnown()
    {
        var result = SpeakerTurnAssembler.Assign([Word("Hello", 1, 2)], "microphone", null, "unused");
        Assert.Equal("self", Assert.Single(result).SpeakerLabel);
    }

    [Fact]
    public void OutputUsesGreatestOverlapAndMarksEqualOverlapUnresolved()
    {
        var diarization = Result([new(0, 1.5, 0), new(1.5, 3, 1)]);
        var result = SpeakerTurnAssembler.Assign([Word("first", 0, 1), Word("tied", 1, 2), Word("last", 2, 3)], "system_output", diarization, "chunk:");
        Assert.Equal(["chunk:0", "speaker_unresolved", "chunk:1"], result.Select(static item => item.SpeakerLabel));
    }

    [Fact]
    public void NearbyToleranceIsBoundedAndTextOnlyCannotComplete()
    {
        var result = SpeakerTurnAssembler.Assign([Word("far", 5, 6)], "system_output", Result([new(0, 1, 0)]), "chunk:");
        Assert.Equal("speaker_unresolved", Assert.Single(result).SpeakerLabel);
        var error = Assert.Throws<SpeakerProcessingException>(() => SpeakerTurnAssembler.Assign([new TranscriptionSegment("No timing")], "microphone", null, ""));
        Assert.Equal("timestamp_capability_missing", error.Code);
        Assert.Throws<SpeakerProcessingException>(() => SpeakerTurnAssembler.Assign([Word("speech", 0, 1)], "system_output", null, ""));
    }

    [Fact]
    public void WordOutsideAudioCannotBecomeACompletedSpeakerTurn()
    {
        var error = Assert.Throws<SpeakerProcessingException>(() => SpeakerTurnAssembler.Assign(
            [Word("outside", 9, 12)], "microphone", Result([new(0, 1, 0)]), ""));
        Assert.Equal("timestamp_capability_missing", error.Code);
    }

    [Fact]
    public void ThreeVoicesKeepOrdinalsAcrossChunksAndReplayedState()
    {
        static int[] Replay()
        {
            var registry = new MeetingSpeakerRegistry();
            var first = new HashSet<int>();
            var result = new List<int> { registry.Resolve(Vector(0), first), registry.Resolve(Vector(1), first), registry.Resolve(Vector(2), first) };
            var next = new HashSet<int>();
            result.Add(registry.Resolve(Vector(2), next));
            result.Add(registry.Resolve(Vector(0), next));
            result.Add(registry.Resolve(Vector(1), next));
            return result.ToArray();
        }
        Assert.Equal([1, 2, 3, 3, 1, 2], Replay());
        Assert.Equal(Replay(), Replay());
    }

    [Fact]
    public void TwoDistinctClustersCannotCollapseWithinTheSameChunk()
    {
        var registry = new MeetingSpeakerRegistry();
        var used = new HashSet<int>();
        Assert.Equal(1, registry.Resolve(Vector(0), used));
        Assert.Equal(2, registry.Resolve(Vector(0), used));
    }

    [Fact]
    public void WordSeamsRetainWordsFromBothSidesOfALongProviderSegment()
    {
        var first = new CompletedTranscriptionChunk(new("a", 0, TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.Zero, 10),
            TranscriptionResult.Completed("first duplicated", segments: [new TranscriptionSegment("first duplicated", TimeSpan.Zero, TimeSpan.FromSeconds(10), "self", [new("first", TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8)), new("duplicated", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10))])]));
        var second = new CompletedTranscriptionChunk(new("b", 1, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(2), 10),
            TranscriptionResult.Completed("duplicated last", segments: [new TranscriptionSegment("duplicated last", TimeSpan.Zero, TimeSpan.FromSeconds(10), "self", [new("duplicated", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), new("last", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4))])]));
        var result = new TranscriptMerger().Merge([first, second]);
        Assert.Equal("first duplicated last", result.Text);
        Assert.Equal([7d, 9d, 11d], result.Segments.Select(static item => item.Start!.Value.TotalSeconds));
    }

    [Fact]
    public void OverlappingVoicesAreSeparateTurns()
    {
        var turns = SpeakerTurnAssembler.GroupTurns([Word("Hello", 0, 1, "self"), Word("yes", 0.5, 2, "remote:1"), Word("again", 2, 3, "self")]);
        Assert.Equal(3, turns.Count);
        Assert.Equal(["self", "remote:1", "self"], turns.Select(static turn => turn.SpeakerLabel));
    }

    [Theory]
    [InlineData("ru", "ru", "Я", "Собеседник 1")]
    [InlineData("en", "en", "Me", "Speaker 1")]
    [InlineData("ru", "pt", "Я", "Собеседник 1")]
    public void ArtifactLanguageAndSpeakerIdentityAreIndependent(string ui, string detected, string self, string remote)
    {
        var merged = new MergedTranscript("Olá", detected, "large-v3-turbo",
            [Word("Olá", 1, 2, "self"), Word("Tudo bem?", 2, 3, "remote:1")], [], null);
        var context = new TranscriptDocumentContext(Guid.NewGuid().ToString("N"), Guid.NewGuid(), "Meeting",
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10), "local.whisper", "large-v3-turbo", "auto",
            new(new string('a', 64), 100, TimeSpan.FromSeconds(10), "wav"), SpeakerAware: true, UiLanguage: ui);
        var document = NormalizedTranscriptDocument.Create(context, merged);
        Assert.Equal("speaker-transcript/v1", document.Schema);
        Assert.Equal(detected, document.DetectedLanguage);
        Assert.Equal(["self", "remote:1"], document.Speakers!.Select(static speaker => speaker.Id));
        var markdown = TranscriptMarkdownRenderer.Render(document);
        Assert.Contains($"**{self} · 00:01**", markdown);
        Assert.Contains($"**{remote} · 00:02**", markdown);
        Assert.DoesNotContain("Vector", JsonSerializer.Serialize(document));
    }

    [Fact]
    public void SpeakerResultRejectsInvalidVoiceVectorsAndOutOfRangeIntervals()
    {
        var valid = Result([new(0, 1, 0)]);
        valid.Validate();
        Assert.Throws<InvalidDataException>(() => (valid with { Speakers = [new(0, new float[256])] }).Validate());
        Assert.Throws<InvalidDataException>(() => (valid with { Intervals = [new(0, 400, 0)] }).Validate());
        var nonfinite = Vector(0); nonfinite[1] = float.NaN;
        Assert.Throws<InvalidDataException>(() => (valid with { Speakers = [new(0, nonfinite)] }).Validate());
    }

    [Fact]
    public async Task MissingAssetsFailBeforeStartingTheWorkerOrDownloading()
    {
        var root = Path.Combine(Path.GetTempPath(), "istranscribe-speaker-absent-" + Guid.NewGuid().ToString("N"));
        var client = new SpeakerDiarizationClient(Path.Combine(root, "missing-worker"), new DiarizationAssets(root));
        var error = await Assert.ThrowsAsync<SpeakerProcessingException>(() => client.EnsureReadyAsync(CancellationToken.None).AsTask());
        Assert.Equal("diarization_assets_missing", error.Code);
        Assert.False(Directory.Exists(root));
    }

    private static TranscriptionSegment Word(string text, double start, double end, string? speaker = null) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), speaker, [new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end))]);

    private static float[] Vector(int axis) { var result = new float[256]; result[axis] = 1; return result; }
    private static SpeakerDiarizationResult Result(SpeakerInterval[] intervals) => new(DiarizationAssets.RuntimeVersion,
        DiarizationAssets.ParametersVersion, new string('a', 64), 10, intervals,
        intervals.Select(static item => item.Speaker).Distinct().Select(static id => new SpeakerEmbedding(id, Vector(id))).ToArray());
}

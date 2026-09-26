namespace IsTranscribe.Transcription.Local.Protocol;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
public sealed record SpeakerDiarizationResult(
    string RuntimeVersion,
    string ParametersVersion,
    string InputSha256,
    double DurationSeconds,
    IReadOnlyList<SpeakerInterval> Intervals,
    IReadOnlyList<SpeakerEmbedding> Speakers)
{
    public void Validate()
    {
        if (RuntimeVersion != Models.DiarizationAssets.RuntimeVersion
            || ParametersVersion != Models.DiarizationAssets.ParametersVersion
            || InputSha256.Length != 64 || InputSha256.Any(static c => !Uri.IsHexDigit(c))
            || !double.IsFinite(DurationSeconds) || DurationSeconds is <= 0 or > 300
            || Speakers.Count > 64 || Intervals.Count > 10000
            || Speakers.Select(static x => x.Id).Distinct().Count() != Speakers.Count)
            throw new InvalidDataException("diarization_result_invalid");
        foreach (var speaker in Speakers)
            if (speaker.Id < 0 || speaker.Vector.Length != 256
                || speaker.Vector.Any(static value => !float.IsFinite(value))
                || speaker.Vector.Sum(static value => (double)value * value) < 0.00001)
                throw new InvalidDataException("diarization_embedding_invalid");
        foreach (var interval in Intervals)
            if (!double.IsFinite(interval.Start) || !double.IsFinite(interval.End)
                || interval.Start < 0 || interval.End <= interval.Start
                || interval.End > DurationSeconds + 0.1
                || !Speakers.Any(speaker => speaker.Id == interval.Speaker))
                throw new InvalidDataException("diarization_interval_invalid");
    }
}

public sealed record SpeakerInterval(double Start, double End, int Speaker);
public sealed record SpeakerEmbedding(int Id, float[] Vector);

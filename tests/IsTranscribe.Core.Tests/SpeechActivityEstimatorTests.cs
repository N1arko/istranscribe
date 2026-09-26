using IsTranscribe.Core.Detection;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </summary>
public sealed class SpeechActivityEstimatorTests
{
    private const int SampleRate = 16_000;
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    [Fact]
    public void SilenceDoesNotBecomeSpeech()
    {
        var estimator = new SpeechActivityEstimator();

        AppendInFrames(estimator, new float[SampleRate * 4], Start);
        var summary = estimator.GetSummary(Start.AddSeconds(4));

        Assert.False(summary.IsSustainedSpeech);
        Assert.Equal(0, summary.SpeechProbability);
        Assert.Equal(0, summary.ActiveRatio);
    }

    [Fact]
    public void SustainedVoiceLikeSignalBecomesSpeech()
    {
        var estimator = new SpeechActivityEstimator();

        AppendInFrames(estimator, CreateVoiceLikeSignal(TimeSpan.FromSeconds(4)), Start);
        var summary = estimator.GetSummary(Start.AddSeconds(4));

        Assert.True(summary.IsSustainedSpeech, summary.ToString());
        Assert.True(summary.SpeechProbability >= 0.45, summary.ToString());
        Assert.True(summary.ActiveRatio >= 0.26, summary.ToString());
    }

    [Fact]
    public void ShortVoiceBurstDoesNotBecomeSustainedSpeech()
    {
        var estimator = new SpeechActivityEstimator();

        AppendInFrames(estimator, CreateVoiceLikeSignal(TimeSpan.FromSeconds(1)), Start);
        var summary = estimator.GetSummary(Start.AddSeconds(1));

        Assert.False(summary.IsSustainedSpeech, summary.ToString());
        Assert.True(summary.ObservedDuration < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SteadyToneDoesNotBecomeSpeech()
    {
        var estimator = new SpeechActivityEstimator();
        var tone = Enumerable.Range(0, SampleRate * 4)
            .Select(index => (float)(0.25 * Math.Sin(2 * Math.PI * 440 * index / SampleRate)))
            .ToArray();

        AppendInFrames(estimator, tone, Start);
        var summary = estimator.GetSummary(Start.AddSeconds(4));

        Assert.False(summary.IsSustainedSpeech, summary.ToString());
    }

    [Fact]
    public void WhiteNoiseDoesNotBecomeSpeech()
    {
        var estimator = new SpeechActivityEstimator();
        var random = new Random(42);
        var noise = Enumerable.Range(0, SampleRate * 4)
            .Select(_ => (float)((random.NextDouble() - 0.5) * 0.25))
            .ToArray();

        AppendInFrames(estimator, noise, Start);
        var summary = estimator.GetSummary(Start.AddSeconds(4));

        Assert.False(summary.IsSustainedSpeech, summary.ToString());
    }

    [Fact]
    public void RetainedAnalysisWindowIsBounded()
    {
        var estimator = new SpeechActivityEstimator();

        AppendInFrames(estimator, CreateVoiceLikeSignal(TimeSpan.FromSeconds(20)), Start);
        var summary = estimator.GetSummary(Start.AddSeconds(20));

        Assert.InRange(summary.ObservedDuration, TimeSpan.FromSeconds(5.9), TimeSpan.FromSeconds(6.1));
    }

    private static void AppendInFrames(
        SpeechActivityEstimator estimator,
        IReadOnlyList<float> samples,
        DateTimeOffset start)
    {
        const int samplesPerFrame = SampleRate / 50;
        for (var offset = 0; offset < samples.Count; offset += samplesPerFrame)
        {
            var count = Math.Min(samplesPerFrame, samples.Count - offset);
            var frame = new float[count];
            for (var index = 0; index < count; index++)
            {
                frame[index] = samples[offset + index];
            }

            estimator.AppendMonoSamples(
                frame,
                SampleRate,
                start + TimeSpan.FromSeconds((double)offset / SampleRate));
        }
    }

    private static float[] CreateVoiceLikeSignal(TimeSpan duration)
    {
        var samples = new float[(int)(SampleRate * duration.TotalSeconds)];
        for (var index = 0; index < samples.Length; index++)
        {
            var time = (double)index / SampleRate;
            var syllablePosition = time % 0.72;
            if (syllablePosition > 0.57)
            {
                continue;
            }

            var fundamental = 128 + (32 * Math.Sin(2 * Math.PI * 0.7 * time));
            var envelope = 0.12 + (0.88 * Math.Pow(Math.Sin(Math.PI * syllablePosition / 0.57), 2));
            var voiced = Math.Sin(2 * Math.PI * fundamental * time) +
                         (0.55 * Math.Sin(2 * Math.PI * fundamental * 2 * time)) +
                         (0.30 * Math.Sin(2 * Math.PI * fundamental * 3 * time)) +
                         (0.19 * Math.Sin(2 * Math.PI * fundamental * 5 * time)) +
                         (0.14 * Math.Sin(2 * Math.PI * fundamental * 7 * time)) +
                         (0.10 * Math.Sin(2 * Math.PI * fundamental * 11 * time)) +
                         (0.06 * Math.Sin(2 * Math.PI * fundamental * 17 * time));
            samples[index] = (float)(0.16 * envelope * voiced);
        }

        return samples;
    }
}

namespace IsTranscribe.Core.Detection;

/// <summary>
/// Reduces short mono sample frames to a bounded, platform-neutral speech activity summary.
/// Raw samples are discarded immediately after feature extraction.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public sealed class SpeechActivityEstimator(SpeechActivityEstimatorOptions? options = null)
{
    private const double SilenceDbfs = -120;
    private readonly object _gate = new();
    private readonly SpeechActivityEstimatorOptions _options = options ?? SpeechActivityEstimatorOptions.Default;
    private readonly List<float> _pendingSamples = [];
    private readonly Queue<SpeechFeatureFrame> _features = [];
    private DateTimeOffset? _pendingStartsAtUtc;
    private int _sampleRate;
    private double _noiseFloorDbfs = -65;

    public void AppendMonoSamples(
        ReadOnlySpan<float> samples,
        int sampleRate,
        DateTimeOffset firstSampleAtUtc)
    {
        if (sampleRate < 8_000 || sampleRate > 384_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_sampleRate != 0 && _sampleRate != sampleRate)
            {
                ResetPendingSamples();
            }

            _sampleRate = sampleRate;
            var expectedStart = _pendingStartsAtUtc is null
                ? firstSampleAtUtc
                : _pendingStartsAtUtc.Value + TimeSpan.FromSeconds((double)_pendingSamples.Count / sampleRate);
            if (_pendingStartsAtUtc is not null &&
                (firstSampleAtUtc - expectedStart).Duration() > _options.MaximumClockDiscontinuity)
            {
                ResetPendingSamples();
            }

            _pendingStartsAtUtc ??= firstSampleAtUtc;
            for (var index = 0; index < samples.Length; index++)
            {
                _pendingSamples.Add(float.IsFinite(samples[index]) ? samples[index] : 0);
            }

            var samplesPerFrame = Math.Max(1, (int)Math.Round(sampleRate * _options.FrameDuration.TotalSeconds));
            while (_pendingSamples.Count >= samplesPerFrame)
            {
                var frame = new float[samplesPerFrame];
                _pendingSamples.CopyTo(0, frame, 0, samplesPerFrame);
                _pendingSamples.RemoveRange(0, samplesPerFrame);

                var observedAtUtc = _pendingStartsAtUtc.Value + _options.FrameDuration;
                _pendingStartsAtUtc = observedAtUtc;
                _features.Enqueue(Analyze(frame, sampleRate, observedAtUtc));
                Trim(observedAtUtc);
            }
        }
    }

    public MeetingSpeechActivitySummary GetSummary(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            Trim(nowUtc);
            if (_features.Count == 0)
            {
                return MeetingSpeechActivitySummary.Empty;
            }

            var frames = _features.ToArray();
            var activeFrames = frames
                .Where(frame => frame.SpeechProbability >= _options.ActiveFrameProbability)
                .ToArray();
            var activeRatio = (double)activeFrames.Length / frames.Length;
            var activeProbability = activeFrames.Length == 0
                ? 0
                : activeFrames.Average(static frame => frame.SpeechProbability);
            var speechProbability = Math.Clamp(
                (activeProbability * 0.65) + (activeRatio * 0.35),
                0,
                1);
            var observedDuration = frames.Length == 1
                ? _options.FrameDuration
                : (frames[^1].ObservedAtUtc - frames[0].ObservedAtUtc) + _options.FrameDuration;
            var sustained = observedDuration >= _options.MinimumObservation &&
                            activeRatio >= _options.MinimumActiveRatio &&
                            speechProbability >= _options.MinimumSpeechProbability;

            return new MeetingSpeechActivitySummary(
                speechProbability,
                sustained,
                observedDuration,
                activeRatio);
        }
    }

    public bool IsSpeechActive(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            Trim(nowUtc);
            if (_features.Count == 0)
            {
                return false;
            }

            var latest = _features.Last();
            return nowUtc - latest.ObservedAtUtc <= _options.MaximumClockDiscontinuity &&
                   latest.SpeechProbability >= _options.ActiveFrameProbability;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ResetPendingSamples();
            _features.Clear();
            _sampleRate = 0;
            _noiseFloorDbfs = -65;
        }
    }

    private SpeechFeatureFrame Analyze(float[] source, int sourceSampleRate, DateTimeOffset observedAtUtc)
    {
        var mean = source.Average(static sample => (double)sample);
        double squaredSum = 0;
        double peak = 0;
        foreach (var sample in source)
        {
            var centered = sample - mean;
            squaredSum += centered * centered;
            peak = Math.Max(peak, Math.Abs(centered));
        }

        var rms = Math.Sqrt(squaredSum / source.Length);
        var dbfs = rms <= double.Epsilon ? SilenceDbfs : 20 * Math.Log10(rms);
        var analysisStride = Math.Max(1, sourceSampleRate / 16_000);
        var analysisSampleRate = sourceSampleRate / analysisStride;
        var analysis = new double[(source.Length + analysisStride - 1) / analysisStride];
        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex += analysisStride)
        {
            analysis[sourceIndex / analysisStride] = source[sourceIndex] - mean;
        }

        var zeroCrossingRate = CalculateZeroCrossingRate(analysis);
        var periodicity = CalculatePeriodicity(analysis, analysisSampleRate);
        var (spectralEntropy, spectralCentroid, spectralConcentration) =
            CalculateSpectrumShape(analysis, analysisSampleRate);
        var crestFactor = rms <= double.Epsilon ? 0 : peak / rms;

        var energyThreshold = Math.Max(-52, _noiseFloorDbfs + 8);
        var energyScore = Normalize(dbfs, energyThreshold, energyThreshold + 12);
        var periodicityScore = Normalize(periodicity, 0.18, 0.72);
        var entropyScore = Triangle(spectralEntropy, 0.28, 0.64, 0.94);
        var centroidScore = Triangle(spectralCentroid, 0.04, 0.28, 0.68);
        var concentrationScore = 1 - Normalize(spectralConcentration, 0.16, 0.58);
        var crossingScore = Triangle(zeroCrossingRate, 0.008, 0.09, 0.30);
        var crestScore = Triangle(crestFactor, 1.45, 2.8, 8.5);
        var shapeScore = (periodicityScore * 0.20) +
                         (entropyScore * 0.24) +
                         (centroidScore * 0.12) +
                         (concentrationScore * 0.22) +
                         (crossingScore * 0.10) +
                         (crestScore * 0.12);
        var probability = Math.Clamp(energyScore * shapeScore, 0, 1);

        if (probability < 0.30 || dbfs < energyThreshold)
        {
            var boundedNoise = Math.Clamp(dbfs, -90, -30);
            _noiseFloorDbfs = (_noiseFloorDbfs * 0.98) + (boundedNoise * 0.02);
        }

        return new SpeechFeatureFrame(observedAtUtc, probability);
    }

    private static double CalculateZeroCrossingRate(IReadOnlyList<double> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        var crossings = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            if ((samples[index - 1] < 0 && samples[index] >= 0) ||
                (samples[index - 1] >= 0 && samples[index] < 0))
            {
                crossings++;
            }
        }

        return (double)crossings / (samples.Count - 1);
    }

    private static double CalculatePeriodicity(IReadOnlyList<double> samples, int sampleRate)
    {
        if (samples.Count < 4)
        {
            return 0;
        }

        var minimumLag = Math.Max(1, sampleRate / 400);
        var maximumLag = Math.Min(samples.Count / 2, sampleRate / 75);
        double best = 0;
        for (var lag = minimumLag; lag <= maximumLag; lag++)
        {
            double correlation = 0;
            double leadingEnergy = 0;
            double trailingEnergy = 0;
            for (var index = lag; index < samples.Count; index++)
            {
                var leading = samples[index];
                var trailing = samples[index - lag];
                correlation += leading * trailing;
                leadingEnergy += leading * leading;
                trailingEnergy += trailing * trailing;
            }

            var denominator = Math.Sqrt(leadingEnergy * trailingEnergy);
            if (denominator > double.Epsilon)
            {
                best = Math.Max(best, correlation / denominator);
            }
        }

        return Math.Clamp(best, 0, 1);
    }

    private static (double Entropy, double Centroid, double Concentration) CalculateSpectrumShape(
        IReadOnlyList<double> samples,
        int sampleRate)
    {
        var nyquist = sampleRate / 2d;
        var maximumFrequency = Math.Min(4_000, nyquist * 0.92);
        const double minimumFrequency = 150;
        if (maximumFrequency <= minimumFrequency)
        {
            return (0, 0, 1);
        }

        var frequencyStep = (double)sampleRate / samples.Count;
        var minimumBin = Math.Max(1, (int)Math.Ceiling(minimumFrequency / frequencyStep));
        var maximumBin = Math.Min(samples.Count / 2, (int)Math.Floor(maximumFrequency / frequencyStep));
        var powers = new double[maximumBin - minimumBin + 1];
        double totalPower = 0;
        double weightedFrequency = 0;
        double maximumPower = 0;
        for (var bin = minimumBin; bin <= maximumBin; bin++)
        {
            var frequency = bin * frequencyStep;
            var power = CalculateGoertzelPower(samples, bin);
            powers[bin - minimumBin] = power;
            totalPower += power;
            weightedFrequency += power * frequency;
            maximumPower = Math.Max(maximumPower, power);
        }

        if (totalPower <= double.Epsilon)
        {
            return (0, 0, 1);
        }

        double entropy = 0;
        foreach (var power in powers)
        {
            var proportion = power / totalPower;
            if (proportion > double.Epsilon)
            {
                entropy -= proportion * Math.Log(proportion);
            }
        }

        entropy /= Math.Log(powers.Length);
        var centroid = (weightedFrequency / totalPower) / maximumFrequency;
        var concentration = maximumPower / totalPower;
        return (
            Math.Clamp(entropy, 0, 1),
            Math.Clamp(centroid, 0, 1),
            Math.Clamp(concentration, 0, 1));
    }

    private static double CalculateGoertzelPower(
        IReadOnlyList<double> samples,
        int frequencyBin)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequencyBin / samples.Count);
        double previous = 0;
        double previousPrevious = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            var window = samples.Count == 1
                ? 1
                : 0.5 - (0.5 * Math.Cos(2 * Math.PI * index / (samples.Count - 1)));
            var current = (samples[index] * window) + (coefficient * previous) - previousPrevious;
            previousPrevious = previous;
            previous = current;
        }

        return Math.Max(
            0,
            (previous * previous) + (previousPrevious * previousPrevious) -
            (coefficient * previous * previousPrevious));
    }

    private void Trim(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc - _options.AnalysisWindow;
        while (_features.TryPeek(out var frame) && frame.ObservedAtUtc < cutoff)
        {
            _features.Dequeue();
        }
    }

    private void ResetPendingSamples()
    {
        _pendingSamples.Clear();
        _pendingStartsAtUtc = null;
    }

    private static double Normalize(double value, double minimum, double maximum) =>
        Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);

    private static double Triangle(double value, double minimum, double ideal, double maximum)
    {
        if (value <= minimum || value >= maximum)
        {
            return 0;
        }

        return value <= ideal
            ? Normalize(value, minimum, ideal)
            : Normalize(maximum - value, 0, maximum - ideal);
    }

    private sealed record SpeechFeatureFrame(
        DateTimeOffset ObservedAtUtc,
        double SpeechProbability);
}

public sealed record SpeechActivityEstimatorOptions(
    TimeSpan FrameDuration,
    TimeSpan AnalysisWindow,
    TimeSpan MinimumObservation,
    TimeSpan MaximumClockDiscontinuity,
    double ActiveFrameProbability,
    double MinimumActiveRatio,
    double MinimumSpeechProbability)
{
    public static SpeechActivityEstimatorOptions Default { get; } = new(
        FrameDuration: TimeSpan.FromMilliseconds(20),
        AnalysisWindow: TimeSpan.FromSeconds(6),
        MinimumObservation: TimeSpan.FromSeconds(2),
        MaximumClockDiscontinuity: TimeSpan.FromMilliseconds(250),
        ActiveFrameProbability: 0.38,
        MinimumActiveRatio: 0.26,
        MinimumSpeechProbability: 0.43);
}

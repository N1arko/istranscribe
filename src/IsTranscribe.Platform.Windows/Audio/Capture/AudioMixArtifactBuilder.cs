using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace IsTranscribe.Host.Audio.Capture;

/// <summary>
/// Builds the canonical, encoder-ready PCM artifact from every healthy capture source.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
/// </remarks>
public sealed class AudioMixArtifactBuilder : IAudioArtifactPreparer
{
    public const int CanonicalSampleRate = 48_000;
    public const int CanonicalBitsPerSample = 16;
    public const int CanonicalChannels = 2;

    private const int MixBufferSampleCount = 8_192;
    private const float SingleSourceGain = 0.95f;
    private const float DualSourceGain = 0.625f;
    private const float LimiterCeiling = 0.95f;
    private static readonly WaveFormat CanonicalWaveFormat =
        new(CanonicalSampleRate, CanonicalBitsPerSample, CanonicalChannels);

    public ValueTask<AudioCaptureArtifact?> PrepareAsync(
        AudioCaptureRequest request,
        IReadOnlyList<AudioCaptureArtifact> sourceArtifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceArtifacts);
        cancellationToken.ThrowIfCancellationRequested();

        return new ValueTask<AudioCaptureArtifact?>(Task.Run(
            () => Prepare(request, sourceArtifacts, cancellationToken),
            cancellationToken));
    }

    private static AudioCaptureArtifact? Prepare(
        AudioCaptureRequest request,
        IReadOnlyList<AudioCaptureArtifact> sourceArtifacts,
        CancellationToken cancellationToken)
    {
        var readers = new List<WaveFileReader>(capacity: 2);
        var timelineSegments = new List<PreparedSourceSegment>();
        var mixPath = Path.Combine(request.TempSessionDirectoryPath, "mix.wav");
        var partialPath = Path.Combine(request.TempSessionDirectoryPath, "mix.wav.partial");

        try
        {
            foreach (var artifact in SelectSources(sourceArtifacts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (artifact.RelativeStartOffset < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(sourceArtifacts),
                        artifact.RelativeStartOffset,
                        "Source timeline offsets cannot be negative.");
                }

                var source = TryOpenSource(artifact, readers);
                if (source is not null)
                {
                    timelineSegments.Add(new PreparedSourceSegment(artifact.Kind, source));
                }
            }

            if (timelineSegments.Count == 0)
            {
                return null;
            }

            Directory.CreateDirectory(request.TempSessionDirectoryPath);
            TryDeleteFile(partialPath);

            var sourceRoles = timelineSegments
                .GroupBy(static segment => segment.Kind)
                .Select(static group => CombineSegments(group.Select(static segment => segment.Provider).ToArray()))
                .ToArray();
            var perSourceGain = sourceRoles.Length == 1 ? SingleSourceGain : DualSourceGain;
            var gainedSources = sourceRoles
                .Select(source => (ISampleProvider)new VolumeSampleProvider(source) { Volume = perSourceGain })
                .ToArray();
            ISampleProvider mixed = gainedSources.Length == 1
                ? gainedSources[0]
                : new MixingSampleProvider(gainedSources);

            var floatBuffer = new float[MixBufferSampleCount];
            var pcmBuffer = new byte[MixBufferSampleCount * sizeof(short)];
            long samplesWritten = 0;

            using (var writer = new WaveFileWriter(partialPath, CanonicalWaveFormat))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var samplesRead = mixed.Read(floatBuffer, 0, floatBuffer.Length);
                    if (samplesRead == 0)
                    {
                        break;
                    }

                    if (samplesRead % CanonicalChannels != 0)
                    {
                        throw new InvalidDataException("Prepared audio ended with an incomplete sample frame.");
                    }

                    ConvertToLimitedPcm16(floatBuffer, pcmBuffer, samplesRead);
                    writer.Write(pcmBuffer, 0, checked(samplesRead * sizeof(short)));
                    samplesWritten += samplesRead;
                }

                writer.Flush();
            }

            if (samplesWritten == 0)
            {
                TryDeleteFile(partialPath);
                return null;
            }

            File.Move(partialPath, mixPath, overwrite: true);
            var fileInfo = new FileInfo(mixPath);
            return new AudioCaptureArtifact(
                AudioCaptureArtifactKind.Mixed,
                mixPath,
                fileInfo.Length,
                DateTimeOffset.UtcNow,
                RelativeStartOffset: TimeSpan.Zero);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            TryDeleteFile(partialPath);
        }
    }

    private static IEnumerable<AudioCaptureArtifact> SelectSources(
        IReadOnlyList<AudioCaptureArtifact> sourceArtifacts) => sourceArtifacts
        .Where(static artifact => artifact.Kind is AudioCaptureArtifactKind.Output or AudioCaptureArtifactKind.Microphone)
        .Where(static artifact => !string.IsNullOrWhiteSpace(artifact.Path) && File.Exists(artifact.Path))
        .DistinctBy(static artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
        .OrderBy(static artifact => artifact.Kind)
        .ThenBy(static artifact => artifact.RelativeStartOffset)
        .ThenBy(static artifact => artifact.Path, StringComparer.OrdinalIgnoreCase);

    private static ISampleProvider CombineSegments(IReadOnlyList<ISampleProvider> segments) => segments.Count == 1
        ? segments[0]
        : new MixingSampleProvider(segments);

    private static ISampleProvider? TryOpenSource(
        AudioCaptureArtifact artifact,
        ICollection<WaveFileReader> readers)
    {
        WaveFileReader? reader = null;
        try
        {
            // Recovery can observe durable PCM beyond the last periodic RIFF checkpoint.
            // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
            _ = WaveArtifactRepair.TryRepair(artifact.Path);
            reader = new WaveFileReader(artifact.Path);
            if (reader.TotalTime <= TimeSpan.Zero)
            {
                reader.Dispose();
                return null;
            }

            readers.Add(reader);
            ISampleProvider current = ToStereo(reader.ToSampleProvider());
            if (current.WaveFormat.SampleRate != CanonicalSampleRate)
            {
                current = new WdlResamplingSampleProvider(current, CanonicalSampleRate);
            }

            if (artifact.RelativeStartOffset > TimeSpan.Zero)
            {
                current = new OffsetSampleProvider(current)
                {
                    DelayBy = artifact.RelativeStartOffset
                };
            }

            return current;
        }
        catch (Exception exception) when (IsUnreadableSource(exception))
        {
            reader?.Dispose();
            return null;
        }
    }

    private static ISampleProvider ToStereo(ISampleProvider source) => source.WaveFormat.Channels switch
    {
        CanonicalChannels => source,
        1 => new MonoToStereoSampleProvider(source),
        > CanonicalChannels => new MultichannelToStereoSampleProvider(source),
        _ => throw new NotSupportedException(
            $"Channel conversion from {source.WaveFormat.Channels} to {CanonicalChannels} is unsupported.")
    };

    private static void ConvertToLimitedPcm16(float[] source, byte[] destination, int sampleCount)
    {
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = float.IsFinite(source[index]) ? source[index] : 0f;
            sample = Math.Clamp(sample, -LimiterCeiling, LimiterCeiling);
            var pcmSample = (short)MathF.Round(sample * short.MaxValue);
            var byteIndex = index * sizeof(short);
            destination[byteIndex] = unchecked((byte)pcmSample);
            destination[byteIndex + 1] = unchecked((byte)(pcmSample >> 8));
        }
    }

    private static bool IsUnreadableSource(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException
            or NotSupportedException
            or ArgumentException;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed cleanup must not hide the original preparation result.
        }
    }

    private sealed class MultichannelToStereoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _inputBuffer = [];

        public MultichannelToStereoSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, CanonicalChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requestedFrames = count / CanonicalChannels;
            if (requestedFrames == 0)
            {
                return 0;
            }

            var inputChannels = _source.WaveFormat.Channels;
            var requestedInputSamples = checked(requestedFrames * inputChannels);
            if (_inputBuffer.Length < requestedInputSamples)
            {
                _inputBuffer = new float[requestedInputSamples];
            }

            var inputSamplesRead = _source.Read(_inputBuffer, 0, requestedInputSamples);
            var framesRead = inputSamplesRead / inputChannels;
            for (var frame = 0; frame < framesRead; frame++)
            {
                var inputBase = frame * inputChannels;
                var sum = 0f;
                for (var channel = 0; channel < inputChannels; channel++)
                {
                    sum += _inputBuffer[inputBase + channel];
                }

                var mono = sum / inputChannels;
                var outputBase = offset + (frame * CanonicalChannels);
                buffer[outputBase] = mono;
                buffer[outputBase + 1] = mono;
            }

            return framesRead * CanonicalChannels;
        }
    }

    private sealed record PreparedSourceSegment(
        AudioCaptureArtifactKind Kind,
        ISampleProvider Provider);
}

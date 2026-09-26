using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IsTranscribe.Application.Recording;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace IsTranscribe.Platform.Windows.Audio.Encoding;

/// <summary>
/// Encodes a canonical speech-first MPEG-1 Layer III stream into an explicit MP3 container.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#compression
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.implementation
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#finalization
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#verification
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMediaFoundationMp3Encoder : IAudioArtifactEncoder
{
    public const int CanonicalSampleRate = 48_000;
    public const int CanonicalChannels = 2;
    public const int CanonicalBitsPerSample = 16;
    public const int TargetBitRate = 128_000;

    private const int EncoderReadBufferBytes = 64 * 1024;
    private const string CodecName = "windows-media-foundation-mp3";

    private static readonly SemaphoreSlim EncodeGate = new(1, 1);
    private readonly object _availabilityGate = new();
    private AudioArtifactEncoderAvailability? _availability;

    public AudioArtifactEncoderAvailability ProbeAvailability()
    {
        lock (_availabilityGate)
        {
            return _availability ??= ProbeAvailabilityCore();
        }
    }

    public Task<AudioArtifactEncodeResult> EncodeAsync(
        string sourceWavePath,
        string partialOutputPath,
        CancellationToken cancellationToken) =>
        EncodeAsync(
            new AudioArtifactEncodeRequest(sourceWavePath, partialOutputPath),
            progress: null,
            cancellationToken);

    public async Task<AudioArtifactEncodeResult> EncodeAsync(
        AudioArtifactEncodeRequest request,
        IProgress<AudioArtifactEncodingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var availability = ProbeAvailability();
        if (!availability.IsAvailable)
        {
            return Failed(
                availability.ReasonCode ?? "mp3_encoder_unavailable",
                availability.Detail ?? "The Windows MP3 encoder is unavailable.");
        }

        await EncodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(
                    () => EncodeCore(request, progress, cancellationToken),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            EncodeGate.Release();
        }
    }

    public AudioArtifactReadabilityProbe ProbeReadability(string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return Unreadable("artifact_path_missing", "An artifact path is required.");
        }

        var fullPath = Path.GetFullPath(artifactPath);
        if (!File.Exists(fullPath))
        {
            return Unreadable("artifact_not_found", "The encoded artifact does not exist.");
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length < 4 || !HasMp3StreamSignature(fullPath))
            {
                return Unreadable(
                    "mp3_stream_invalid",
                    "The artifact does not contain an ID3 tag or MPEG-1 Layer III frame.",
                    fileInfo.Length);
            }

            using var reader = new MediaFoundationReader(fullPath);
            var buffer = new byte[Math.Min(64 * 1024, Math.Max(reader.WaveFormat.BlockAlign, reader.WaveFormat.AverageBytesPerSecond / 10))];
            var decodedBytes = reader.Read(buffer, 0, buffer.Length);
            if (reader.TotalTime <= TimeSpan.Zero || decodedBytes <= 0)
            {
                return Unreadable(
                    "encoded_audio_empty",
                    "The MP3 stream has no readable audio samples.",
                    fileInfo.Length);
            }

            return new AudioArtifactReadabilityProbe(
                IsReadable: true,
                fileInfo.Length,
                reader.TotalTime,
                reader.WaveFormat.SampleRate,
                reader.WaveFormat.Channels,
                reader.WaveFormat.BitsPerSample,
                ReasonCode: null,
                Detail: null);
        }
        catch (Exception exception) when (exception is COMException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or PlatformNotSupportedException
                                          or DllNotFoundException
                                          or EntryPointNotFoundException
                                          or TypeInitializationException)
        {
            return Unreadable(
                "encoded_audio_unreadable",
                $"{exception.GetType().Name}: {exception.Message}",
                TryGetFileLength(fullPath));
        }
    }

    internal static IWaveProvider CreateCanonicalProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        ISampleProvider current = source;
        if (current.WaveFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"Only mono and stereo sources are supported; received {current.WaveFormat.Channels} channels.");
        }

        if (current.WaveFormat.SampleRate != CanonicalSampleRate)
        {
            current = new WdlResamplingSampleProvider(current, CanonicalSampleRate);
        }

        if (current.WaveFormat.Channels == 1)
        {
            current = new MonoToStereoSampleProvider(current);
        }

        return new SampleToWaveProvider16(current);
    }

    private static AudioArtifactEncoderAvailability ProbeAvailabilityCore()
    {
        try
        {
            var canonicalFormat = new WaveFormat(
                CanonicalSampleRate,
                CanonicalBitsPerSample,
                CanonicalChannels);
            var mediaType = MediaFoundationEncoder.SelectMediaType(
                AudioSubtypes.MFAudioFormat_MP3,
                canonicalFormat,
                TargetBitRate);
            if (mediaType is null)
            {
                return Unavailable(
                    "mp3_encoder_unavailable",
                    "Media Foundation exposed no MP3 media type for 48 kHz stereo PCM.");
            }

            var selectedBitRate = mediaType.AverageBytesPerSecond * 8;
            using var encoder = new MediaFoundationEncoder(mediaType);
            return selectedBitRate == TargetBitRate
                ? new AudioArtifactEncoderAvailability(
                    IsAvailable: true,
                    CodecName,
                    CanonicalSampleRate,
                    CanonicalChannels,
                    CanonicalBitsPerSample,
                    selectedBitRate,
                    ReasonCode: null,
                    Detail: null)
                : Unavailable(
                    "mp3_128kbps_unavailable",
                    $"Media Foundation selected {selectedBitRate} bps instead of {TargetBitRate} bps.");
        }
        catch (Exception exception) when (exception is COMException
                                          or PlatformNotSupportedException
                                          or InvalidOperationException
                                          or DllNotFoundException
                                          or EntryPointNotFoundException
                                          or TypeInitializationException)
        {
            return Unavailable(
                "media_foundation_unavailable",
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private AudioArtifactEncodeResult EncodeCore(
        AudioArtifactEncodeRequest request,
        IProgress<AudioArtifactEncodingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(request.SourceWavePath);
        var partialPath = Path.GetFullPath(request.PartialOutputPath);
        if (!File.Exists(sourcePath))
        {
            return Failed("source_wave_not_found", "The source wave file does not exist.");
        }

        if (File.Exists(partialPath))
        {
            return Failed("partial_output_exists", "The partial output path already exists.");
        }

        var partialCreated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);

            using var reader = new WaveFileReader(sourcePath);
            var canonicalProvider = CreateCanonicalProvider(reader.ToSampleProvider());
            var totalBytes = Math.Max(
                canonicalProvider.WaveFormat.BlockAlign,
                (long)Math.Ceiling(
                    reader.TotalTime.TotalSeconds * canonicalProvider.WaveFormat.AverageBytesPerSecond));
            var cancellableProvider = new ProgressWaveProvider(
                canonicalProvider,
                totalBytes,
                progress,
                cancellationToken);

            var mediaType = MediaFoundationEncoder.SelectMediaType(
                AudioSubtypes.MFAudioFormat_MP3,
                canonicalProvider.WaveFormat,
                TargetBitRate);
            if (mediaType is null)
            {
                return Failed(
                    "mp3_encoder_unavailable",
                    "Media Foundation exposed no compatible MP3 encoder.");
            }

            var selectedBitRate = mediaType.AverageBytesPerSecond * 8;
            if (selectedBitRate != TargetBitRate)
            {
                using var rejectedEncoder = new MediaFoundationEncoder(mediaType);
                return Failed(
                    "mp3_128kbps_unavailable",
                    $"Media Foundation selected {selectedBitRate} bps instead of {TargetBitRate} bps.");
            }

            using (var encoder = new MediaFoundationEncoder(mediaType)
            {
                DefaultReadBufferSize = AlignToBlock(
                    EncoderReadBufferBytes,
                    canonicalProvider.WaveFormat.BlockAlign)
            })
            using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                EncoderReadBufferBytes,
                FileOptions.WriteThrough))
            {
                partialCreated = true;
                encoder.Encode(
                    output,
                    cancellableProvider,
                    TranscodeContainerTypes.MFTranscodeContainerType_MP3);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AudioArtifactEncodingProgress(totalBytes, totalBytes, 1));
            var readability = ProbeReadability(partialPath);
            if (!readability.IsReadable)
            {
                TryDeleteFile(partialPath);
                return Failed(
                    readability.ReasonCode ?? "encoded_audio_unreadable",
                    readability.Detail ?? "The encoded artifact could not be read.");
            }

            return new AudioArtifactEncodeResult(
                Succeeded: true,
                partialPath,
                readability.FileBytes,
                readability.Duration,
                CanonicalSampleRate,
                CanonicalChannels,
                CanonicalBitsPerSample,
                TargetBitRate,
                ErrorCode: null,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            if (partialCreated)
            {
                TryDeleteFile(partialPath);
            }

            throw;
        }
        catch (Exception exception) when (exception is COMException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or NotSupportedException
                                          or PlatformNotSupportedException
                                          or DllNotFoundException
                                          or EntryPointNotFoundException
                                          or TypeInitializationException)
        {
            if (partialCreated)
            {
                TryDeleteFile(partialPath);
            }

            return Failed(
                ClassifyFailure(exception),
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ValidateRequest(AudioArtifactEncodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceWavePath))
        {
            throw new ArgumentException("A source wave path is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PartialOutputPath))
        {
            throw new ArgumentException("A partial output path is required.", nameof(request));
        }

        var sourcePath = Path.GetFullPath(request.SourceWavePath);
        var partialPath = Path.GetFullPath(request.PartialOutputPath);
        if (string.Equals(sourcePath, partialPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and partial output paths must differ.", nameof(request));
        }

        if (!string.Equals(Path.GetExtension(sourcePath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The source artifact must be a wave file.", nameof(request));
        }

        if (!partialPath.EndsWith(".mp3.partial", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The partial output artifact must use the .mp3.partial suffix.",
                nameof(request));
        }
    }

    private static bool HasMp3StreamSignature(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[Math.Min(64 * 1024, checked((int)stream.Length))];
        var bytesRead = stream.Read(header, 0, header.Length);
        if (bytesRead >= 3
            && header[0] == (byte)'I'
            && header[1] == (byte)'D'
            && header[2] == (byte)'3')
        {
            return true;
        }

        for (var offset = 0; offset <= bytesRead - 4; offset++)
        {
            var second = header[offset + 1];
            var third = header[offset + 2];
            var isMpeg1Layer3 = header[offset] == byte.MaxValue
                && (second & 0xE0) == 0xE0
                && (second & 0x18) == 0x18
                && (second & 0x06) == 0x02;
            var hasDefinedBitRate = (third & 0xF0) is not 0x00 and not 0xF0;
            var hasDefinedSampleRate = (third & 0x0C) != 0x0C;
            if (isMpeg1Layer3 && hasDefinedBitRate && hasDefinedSampleRate)
            {
                return true;
            }
        }

        return false;
    }

    private static int AlignToBlock(int bufferBytes, int blockAlign) =>
        Math.Max(blockAlign, bufferBytes - (bufferBytes % blockAlign));

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        NotSupportedException => "source_format_unsupported",
        COMException => "media_foundation_encode_failed",
        PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException =>
            "media_foundation_unavailable",
        UnauthorizedAccessException => "artifact_access_denied",
        IOException => "artifact_io_failed",
        _ => "mp3_encode_failed"
    };

    private static AudioArtifactEncodeResult Failed(string code, string message) => new(
        Succeeded: false,
        PartialOutputPath: null,
        OutputBytes: 0,
        Duration: null,
        SampleRate: null,
        Channels: null,
        BitsPerSample: null,
        BitRate: null,
        ErrorCode: code,
        ErrorMessage: message);

    private static AudioArtifactEncoderAvailability Unavailable(string code, string detail) => new(
        IsAvailable: false,
        CodecName,
        CanonicalSampleRate,
        CanonicalChannels,
        CanonicalBitsPerSample,
        TargetBitRate,
        ReasonCode: code,
        Detail: detail);

    private static AudioArtifactReadabilityProbe Unreadable(
        string code,
        string detail,
        long fileBytes = 0) => new(
        IsReadable: false,
        fileBytes,
        Duration: null,
        SampleRate: null,
        Channels: null,
        BitsPerSample: null,
        ReasonCode: code,
        Detail: detail);

    private static long TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

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
        }
    }

    private sealed class ProgressWaveProvider(
        IWaveProvider source,
        long totalBytes,
        IProgress<AudioArtifactEncodingProgress>? progress,
        CancellationToken cancellationToken) : IWaveProvider
    {
        private long _processedBytes;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            // Let Sink Writer finalize its container normally. EncodeCore observes
            // cancellation immediately afterwards and removes the partial artifact.
            if (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            var read = source.Read(buffer, offset, count);
            if (read > 0)
            {
                _processedBytes += read;
                progress?.Report(new AudioArtifactEncodingProgress(
                    _processedBytes,
                    totalBytes,
                    Math.Clamp((double)_processedBytes / totalBytes, 0, 1)));
            }

            return read;
        }
    }
}

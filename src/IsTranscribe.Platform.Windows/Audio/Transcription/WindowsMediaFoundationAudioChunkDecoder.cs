using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using IsTranscribe.Application.Transcription;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Uses Windows Media Foundation through the existing NAudio boundary to create bounded
/// transcription PCM ranges.
/// </summary>
/// <remarks>
/// The decoder reads system-owned codecs in-process. It never discovers or starts a codec
/// executable and it does not retain a full decoded recording on disk.
///
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#dependencies
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMediaFoundationAudioChunkDecoder : IPlatformAudioChunkDecoder
{
    private const int FramesPerRead = 4_096;
    private const int WaveHeaderLength = 44;
    private const int MinimumWindowsBuild = 19_041;
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static readonly Lazy<WindowsMediaFoundationDecodeCapabilities> SystemCapabilities =
        new(ProbeSystemCapabilities, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool CanDecode(string sourceFormat)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild)
            || string.IsNullOrWhiteSpace(sourceFormat))
        {
            return false;
        }

        return SystemCapabilities.Value.Supports(sourceFormat);
    }

    public async ValueTask<PlatformAudioChunkDecodeResult> DecodeAsync(
        PlatformAudioChunkDecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = request.Validate();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild))
        {
            throw new PlatformNotSupportedException(
                "The Media Foundation transcription decoder requires Windows 10 build 19041 or newer.");
        }

        if (!CanDecode(validated.SourceFormat))
        {
            throw new NotSupportedException(
                $"Windows transcription decoding is unavailable for '{validated.SourceFormat}' audio.");
        }

        var sourcePath = Path.GetFullPath(validated.SourcePath);
        var outputPath = Path.GetFullPath(validated.OutputWavePath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The decoder output must not replace the source audio.",
                nameof(request));
        }

        var sourceExtension = NormalizeFormat(Path.GetExtension(sourcePath));
        if (!AreEquivalentFormats(sourceExtension, validated.SourceFormat))
        {
            throw new InvalidDataException(
                "The transcription source extension does not match its persisted format.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var sourceLease = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            var sourceBefore = CaptureSourceState(sourcePath);
            if (sourceLease.Length != sourceBefore.SizeBytes
                || sourceBefore.SizeBytes != validated.ExpectedSourceSizeBytes)
            {
                throw new InvalidDataException(
                    "The transcription source size no longer matches its job identity.");
            }

            sourceLease.Position = 0;
            var actualSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(sourceLease, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(actualSha256, validated.ExpectedSourceSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The transcription source hash no longer matches its job identity.");
            }

            sourceLease.Position = 0;

            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException(
                    "The decoded PCM output directory is unavailable.");
            Directory.CreateDirectory(outputDirectory);

            var ownsOutput = 0;
            var completed = false;
            try
            {
                var result = await Task.Run(
                        () => DecodeCore(
                            validated with { SourcePath = sourcePath, OutputWavePath = outputPath },
                            sourceLease,
                            cancellationToken,
                            () => Interlocked.Exchange(ref ownsOutput, 1)),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                var sourceAfter = CaptureSourceState(sourcePath);
                if (sourceAfter != sourceBefore || sourceLease.Length != sourceAfter.SizeBytes)
                {
                    throw new InvalidDataException(
                        "The transcription source changed while Media Foundation decoded it.");
                }

                sourceLease.Position = 0;
                var sourceAfterSha256 = Convert.ToHexString(
                        await SHA256.HashDataAsync(sourceLease, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
                if (!string.Equals(sourceAfterSha256, actualSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The transcription source content changed while Media Foundation decoded it.");
                }

                completed = true;
                return result;
            }
            finally
            {
                if (!completed && Volatile.Read(ref ownsOutput) != 0)
                {
                    TryDelete(outputPath);
                }
            }
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static PlatformAudioChunkDecodeResult DecodeCore(
        PlatformAudioChunkDecodeRequest request,
        Stream sourceStream,
        CancellationToken cancellationToken,
        Action outputCreated)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDisposable sourceOwner;
        IWaveProvider sourceProvider;
        if (IsOggOpus(request.SourceFormat))
        {
            var oggReader = new OggOpusRangeWaveProvider(sourceStream, request.Start);
            sourceOwner = oggReader;
            sourceProvider = oggReader;
        }
        else
        {
            var mediaFoundationReader = new TimestampAwareMediaFoundationReader(sourceStream);
            sourceOwner = mediaFoundationReader;
            sourceProvider = mediaFoundationReader;
            ValidateReadableSource(mediaFoundationReader, request);
            if (request.Start > TimeSpan.Zero)
            {
                var sourceStartBytes = AlignDown(
                    checked((long)Math.Floor(
                        request.Start.TotalSeconds * mediaFoundationReader.WaveFormat.AverageBytesPerSecond)),
                    mediaFoundationReader.WaveFormat.BlockAlign);
                var maximumStartBytes = AlignDown(
                    mediaFoundationReader.Length,
                    mediaFoundationReader.WaveFormat.BlockAlign);
                mediaFoundationReader.Position = Math.Clamp(sourceStartBytes, 0, maximumStartBytes);
            }
        }

        using (sourceOwner)
        {
            var outputFormat = new WaveFormat(
                PlatformAudioChunkDecodeRequest.TargetSampleRate,
                PlatformAudioChunkDecodeRequest.TargetBitsPerSample,
                PlatformAudioChunkDecodeRequest.TargetChannels);
            using var resampler = new MediaFoundationResampler(sourceProvider, outputFormat)
            {
                ResamplerQuality = 60
            };

            var requestedFrames = checked((long)Math.Ceiling(
                request.Duration.TotalSeconds * PlatformAudioChunkDecodeRequest.TargetSampleRate));
            if (requestedFrames <= 0)
            {
                throw new InvalidDataException(
                    "The requested transcription range is shorter than one PCM frame.");
            }

            using var output = new FileStream(
                request.OutputWavePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough | FileOptions.SequentialScan);
            outputCreated();
            output.Write(new byte[WaveHeaderLength]);

            var pcm = new byte[FramesPerRead * sizeof(short)];
            long writtenFrames = 0;
            while (writtenFrames < requestedFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var framesToRead = checked((int)Math.Min(
                    FramesPerRead,
                    requestedFrames - writtenFrames));
                var bytesToRead = checked(framesToRead * sizeof(short));
                var bytesRead = resampler.Read(pcm, 0, bytesToRead);
                if (bytesRead == 0)
                {
                    break;
                }

                if (bytesRead < 0 || bytesRead > bytesToRead || bytesRead % sizeof(short) != 0)
                {
                    throw new InvalidDataException(
                        "Media Foundation returned an invalid PCM buffer length.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                output.Write(pcm, 0, bytesRead);
                writtenFrames = checked(writtenFrames + (bytesRead / sizeof(short)));
            }

            if (writtenFrames <= 0)
            {
                throw new InvalidDataException(
                    "Media Foundation produced no PCM samples for the requested range.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var waveHeader = CreateWaveHeader(writtenFrames);
            output.Position = 0;
            output.Write(waveHeader);
            output.Flush(flushToDisk: true);

            var decodedEnd = writtenFrames == requestedFrames
                ? request.End
                : request.Start + TimeSpan.FromSeconds(
                    (double)writtenFrames / PlatformAudioChunkDecodeRequest.TargetSampleRate);
            if (decodedEnd > request.End)
            {
                decodedEnd = request.End;
            }

            return new PlatformAudioChunkDecodeResult(
                request.OutputWavePath,
                PlatformAudioChunkDecodeRequest.TargetSampleRate,
                PlatformAudioChunkDecodeRequest.TargetChannels,
                PlatformAudioChunkDecodeRequest.TargetBitsPerSample,
                writtenFrames,
                request.Start,
                decodedEnd);
        }
    }

    private static void ValidateReadableSource(
        TimestampAwareMediaFoundationReader reader,
        PlatformAudioChunkDecodeRequest request)
    {
        var format = reader.WaveFormat;
        if (format.SampleRate <= 0
            || format.Channels is < 1 or > 2
            || format.BlockAlign <= 0
            || format.AverageBytesPerSecond <= 0)
        {
            throw new InvalidDataException(
                "Media Foundation returned an unsupported source audio layout.");
        }

        if (reader.TotalTime <= TimeSpan.Zero || reader.Length <= 0)
        {
            throw new InvalidDataException(
                "Media Foundation reported an empty source audio stream.");
        }

        if (request.Start >= reader.TotalTime + TimeSpan.FromMilliseconds(1))
        {
            throw new InvalidDataException(
                "The requested transcription range starts past readable audio.");
        }
    }

    private static WindowsMediaFoundationDecodeCapabilities ProbeSystemCapabilities()
    {
        try
        {
            MediaFoundationApi.Startup();
            return new WindowsMediaFoundationDecodeCapabilities(
                PcmWave: true,
                Mp3: HasAudioDecoder(AudioSubtypes.MFAudioFormat_MP3),
                Aac: HasAudioDecoder(AudioSubtypes.MFAudioFormat_AAC),
                Alac: HasAudioDecoder(AudioSubtypes.MFAudioFormat_ALAC),
                Flac: HasAudioDecoder(AudioSubtypes.MFAudioFormat_FLAC),
                OggOpus: true);
        }
        catch (Exception)
        {
            return WindowsMediaFoundationDecodeCapabilities.Empty;
        }
    }

    private static bool HasAudioDecoder(Guid audioSubtype)
    {
        IntPtr activationPointers = IntPtr.Zero;
        var activationCount = 0;
        try
        {
            var inputType = new MFT_REGISTER_TYPE_INFO
            {
                guidMajorType = MediaTypes.MFMediaType_Audio,
                guidSubtype = audioSubtype
            };
            MediaFoundationInterop.MFTEnumEx(
                MediaFoundationTransformCategories.AudioDecoder,
                _MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL,
                inputType,
                null!,
                out activationPointers,
                out activationCount);
            return activationCount > 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (activationPointers != IntPtr.Zero)
            {
                for (var index = 0; index < activationCount; index++)
                {
                    var pointer = Marshal.ReadIntPtr(
                        activationPointers,
                        checked(index * IntPtr.Size));
                    if (pointer != IntPtr.Zero)
                    {
                        Marshal.Release(pointer);
                    }
                }

                Marshal.FreeCoTaskMem(activationPointers);
            }
        }
    }

    private static byte[] CreateWaveHeader(long sampleFrames)
    {
        var dataLength = checked(sampleFrames * sizeof(short));
        if (dataLength > uint.MaxValue - 36L)
        {
            throw new InvalidDataException(
                "The decoded transcription range exceeds the WAV size limit.");
        }

        var header = new byte[WaveHeaderLength];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(4, 4),
            checked((uint)(36 + dataLength)));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(22, 2),
            PlatformAudioChunkDecodeRequest.TargetChannels);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(24, 4),
            PlatformAudioChunkDecodeRequest.TargetSampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(28, 4),
            PlatformAudioChunkDecodeRequest.TargetSampleRate * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(34, 2),
            PlatformAudioChunkDecodeRequest.TargetBitsPerSample);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(40, 4),
            checked((uint)dataLength));
        return header;
    }

    private static SourceFileState CaptureSourceState(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException(
                "The transcription source audio is unavailable.",
                sourcePath);
        }

        return new SourceFileState(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static bool AreEquivalentFormats(string left, string right) =>
        string.Equals(NormalizeWaveAlias(left), NormalizeWaveAlias(right), StringComparison.Ordinal);

    private static string NormalizeWaveAlias(string format)
    {
        var normalized = NormalizeFormat(format);
        return string.Equals(normalized, "wave", StringComparison.Ordinal)
            ? "wav"
            : normalized;
    }

    private static string NormalizeFormat(string format) =>
        format.Trim().TrimStart('.').ToLowerInvariant();

    private static bool IsOggOpus(string format) => NormalizeFormat(format) is "ogg" or "oga" or "opus";

    private static long AlignDown(long value, int alignment) =>
        value - (value % alignment);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct SourceFileState(
        long SizeBytes,
        long LastWriteTimeUtcTicks);

}

internal sealed record WindowsMediaFoundationDecodeCapabilities(
    bool PcmWave,
    bool Mp3,
    bool Aac,
    bool Alac,
    bool Flac,
    bool OggOpus)
{
    public static WindowsMediaFoundationDecodeCapabilities Empty { get; } =
        new(false, false, false, false, false, false);

    public bool Supports(string sourceFormat) => Normalize(sourceFormat) switch
    {
        "wav" or "wave" => PcmWave,
        "mp3" => Mp3,
        "m4a" or "mp4" => Aac || Alac,
        "flac" => Flac,
        "ogg" or "oga" or "opus" => OggOpus,
        _ => false
    };

    private static string Normalize(string sourceFormat) =>
        sourceFormat.Trim().TrimStart('.').ToLowerInvariant();
}

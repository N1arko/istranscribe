using System.Buffers.Binary;
using System.Security.Cryptography;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Application.Transcription;

public sealed record MaterializedAudioChunk(
    string Path,
    string Format,
    string Sha256,
    long SizeBytes,
    bool IsTemporary);

/// <summary>
/// Creates a provider upload artifact for a deterministic source range.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// </remarks>
public interface IAudioChunkMaterializer
{
    ValueTask<MaterializedAudioChunk> MaterializeAsync(
        TranscriptionJobRecord job,
        AudioChunkDescriptor chunk,
        string outputDirectory,
        CancellationToken cancellationToken);

    void DeleteTemporary(MaterializedAudioChunk chunk);
}

/// <summary>
/// Shared materializer for verified source audio. Remote full-source requests can pass through,
/// while local execution and platform-supported ranged requests become bounded 16 kHz mono PCM.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </remarks>
public sealed class ManagedAudioChunkMaterializer : IAudioChunkMaterializer
{
    private const int CopyBufferSize = 64 * 1024;
    private const int MaximumVerifiedSourceCacheEntries = 64;
    private const int PlatformFrameTolerance = 32;
    private static readonly TimeSpan MaximumDecodedEndShortfall = TimeSpan.FromMilliseconds(250);
    private readonly IPlatformAudioChunkDecoder? _platformDecoder;
    private readonly SemaphoreSlim _sourceVerificationGate = new(1, 1);
    private readonly Dictionary<string, VerifiedSourceSnapshot> _verifiedSources = new(StringComparer.Ordinal);
    private long _verificationSequence;

    public ManagedAudioChunkMaterializer(IPlatformAudioChunkDecoder? platformDecoder = null)
    {
        _platformDecoder = platformDecoder;
    }

    public async ValueTask<MaterializedAudioChunk> MaterializeAsync(
        TranscriptionJobRecord job,
        AudioChunkDescriptor chunk,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(chunk);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("A transcription chunk directory is required.", nameof(outputDirectory));
        }

        var sourcePath = Path.GetFullPath(job.InputAudioPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The transcription source audio is unavailable.", sourcePath);
        }

        var sourceDuration = job.InputDurationSeconds is { } seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidDataException("The transcription source duration is unavailable.");
        ValidateChunk(chunk, sourceDuration);
        var sourceFormat = NormalizeFormat(sourcePath);
        var sourceInfo = await VerifySourceAsync(job, sourcePath, cancellationToken).ConfigureAwait(false);
        var fullSource = chunk.Start <= TimeSpan.Zero
            && chunk.End >= sourceDuration - TimeSpan.FromMilliseconds(1);
        var requiresCanonicalPcm = job.ExecutionKind == TranscriptionExecutionKind.Local;
        var platformCanDecode = _platformDecoder?.CanDecode(sourceFormat) == true;
        if (requiresCanonicalPcm && !platformCanDecode)
        {
            throw new NotSupportedException(
                $"A platform PCM decoder for '{sourceFormat}' audio is required for local transcription.");
        }

        if (fullSource && !requiresCanonicalPcm)
        {
            return new MaterializedAudioChunk(
                sourcePath,
                sourceFormat,
                job.InputSha256,
                sourceInfo.Length,
                IsTemporary: false);
        }

        Directory.CreateDirectory(outputDirectory);
        var usePlatformDecoder = platformCanDecode;
        var outputFormat = usePlatformDecoder
            ? "wav"
            : sourceFormat switch
            {
                "mp3" => "mp3",
                "wav" or "wave" => "wav",
                _ => throw new NotSupportedException(
                    $"Bounded chunking for '{sourceFormat}' audio is unavailable.")
            };
        var outputPath = Path.Combine(outputDirectory, $"{chunk.Id}.{outputFormat}");
        if (File.Exists(outputPath))
        {
            if (usePlatformDecoder)
            {
                await ValidateCanonicalPcmWaveAsync(outputPath, chunk, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await DescribeAsync(outputPath, outputFormat, isTemporary: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (usePlatformDecoder)
            {
                var decodeResult = await _platformDecoder!.DecodeAsync(
                        new PlatformAudioChunkDecodeRequest(
                            sourcePath,
                            sourceFormat,
                            job.InputSha256,
                            job.InputSizeBytes,
                            sourceDuration,
                            chunk.Start,
                            chunk.End,
                            temporaryPath),
                        cancellationToken)
                    .ConfigureAwait(false);
                await ValidatePlatformDecodeResultAsync(
                        temporaryPath,
                        chunk,
                        decodeResult,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (outputFormat == "mp3")
            {
                await SliceMp3Async(sourcePath, temporaryPath, chunk, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await SliceWaveAsync(sourcePath, temporaryPath, chunk, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            return await DescribeAsync(outputPath, outputFormat, isTemporary: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public void DeleteTemporary(MaterializedAudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.IsTemporary)
        {
            TryDelete(chunk.Path);
        }
    }

    private async ValueTask<FileInfo> VerifySourceAsync(
        TranscriptionJobRecord job,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (job.InputSizeBytes <= 0
            || string.IsNullOrWhiteSpace(job.InputSha256)
            || job.InputSha256.Length != 64
            || job.InputSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The persisted transcription source fingerprint is invalid.");
        }

        await _sourceVerificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = CaptureSourceState(sourcePath);
            if (before.SizeBytes != job.InputSizeBytes)
            {
                throw new InvalidDataException("The transcription source size changed after the job was queued.");
            }

            if (_verifiedSources.TryGetValue(sourcePath, out var cached)
                && cached.SizeBytes == before.SizeBytes
                && cached.LastWriteTimeUtcTicks == before.LastWriteTimeUtcTicks
                && string.Equals(cached.ExpectedSha256, job.InputSha256, StringComparison.OrdinalIgnoreCase))
            {
                cached = cached with { Sequence = ++_verificationSequence };
                _verifiedSources[sourcePath] = cached;
                return new FileInfo(sourcePath);
            }

            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            var after = CaptureSourceState(sourcePath);
            if (after != before)
            {
                throw new InvalidDataException("The transcription source changed while its fingerprint was verified.");
            }

            if (!string.Equals(actualSha256, job.InputSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The transcription source hash changed after the job was queued.");
            }

            _verifiedSources[sourcePath] = new VerifiedSourceSnapshot(
                before.SizeBytes,
                before.LastWriteTimeUtcTicks,
                job.InputSha256.ToLowerInvariant(),
                ++_verificationSequence);
            TrimVerifiedSourceCache();
            return new FileInfo(sourcePath);
        }
        finally
        {
            _sourceVerificationGate.Release();
        }
    }

    private static SourceFileState CaptureSourceState(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException("The transcription source audio is unavailable.", sourcePath);
        }

        return new SourceFileState(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private void TrimVerifiedSourceCache()
    {
        while (_verifiedSources.Count > MaximumVerifiedSourceCacheEntries)
        {
            var oldest = _verifiedSources.MinBy(static pair => pair.Value.Sequence);
            _verifiedSources.Remove(oldest.Key);
        }
    }

    private static void ValidateChunk(AudioChunkDescriptor chunk, TimeSpan sourceDuration)
    {
        if (string.IsNullOrWhiteSpace(chunk.Id)
            || !string.Equals(chunk.Id, Path.GetFileName(chunk.Id), StringComparison.Ordinal)
            || chunk.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("The transcription chunk identity is unsafe.");
        }

        if (chunk.Start < TimeSpan.Zero || chunk.End <= chunk.Start)
        {
            throw new InvalidDataException("The transcription chunk range is invalid.");
        }

        if (chunk.End > sourceDuration + TimeSpan.FromMilliseconds(1))
        {
            throw new InvalidDataException("The transcription chunk extends past the source duration.");
        }
    }

    private static async ValueTask ValidatePlatformDecodeResultAsync(
        string expectedPath,
        AudioChunkDescriptor chunk,
        PlatformAudioChunkDecodeResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(
                Path.GetFullPath(result.OutputWavePath),
                Path.GetFullPath(expectedPath),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The platform decoder returned an unexpected output path.");
        }

        if (result.SampleRate != PlatformAudioChunkDecodeRequest.TargetSampleRate
            || result.Channels != PlatformAudioChunkDecodeRequest.TargetChannels
            || result.BitsPerSample != PlatformAudioChunkDecodeRequest.TargetBitsPerSample
            || result.SampleFrames <= 0)
        {
            throw new InvalidDataException("The platform decoder returned a non-canonical PCM layout.");
        }

        if ((result.DecodedStart - chunk.Start).Duration() > TimeSpan.FromMilliseconds(1)
            || result.DecodedEnd <= result.DecodedStart
            || result.DecodedEnd > chunk.End + TimeSpan.FromMilliseconds(1)
            || chunk.End - result.DecodedEnd > MaximumDecodedEndShortfall)
        {
            throw new InvalidDataException("The platform decoder returned unexpected time boundaries.");
        }

        var decodedDuration = result.DecodedEnd - result.DecodedStart;
        var expectedFrames = checked((long)Math.Ceiling(
            decodedDuration.TotalSeconds * PlatformAudioChunkDecodeRequest.TargetSampleRate));
        var minimumFrames = Math.Max(1, expectedFrames - PlatformFrameTolerance);
        if (result.SampleFrames < minimumFrames || result.SampleFrames > expectedFrames + 1)
        {
            throw new InvalidDataException("The platform decoder returned a truncated PCM range.");
        }

        var waveFrames = await ValidateCanonicalPcmWaveAsync(expectedPath, chunk, cancellationToken)
            .ConfigureAwait(false);
        if (waveFrames != result.SampleFrames)
        {
            throw new InvalidDataException("The platform decoder result does not match its WAV payload.");
        }
    }

    private static async ValueTask<long> ValidateCanonicalPcmWaveAsync(
        string path,
        AudioChunkDescriptor chunk,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var wave = await ReadWaveLayoutAsync(stream, cancellationToken).ConfigureAwait(false);
        if (wave.FormatBytes.Length < 16
            || BinaryPrimitives.ReadUInt16LittleEndian(wave.FormatBytes.AsSpan(0, 2)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(wave.FormatBytes.AsSpan(2, 2))
                != PlatformAudioChunkDecodeRequest.TargetChannels
            || BinaryPrimitives.ReadUInt32LittleEndian(wave.FormatBytes.AsSpan(4, 4))
                != PlatformAudioChunkDecodeRequest.TargetSampleRate
            || BinaryPrimitives.ReadUInt32LittleEndian(wave.FormatBytes.AsSpan(8, 4))
                != PlatformAudioChunkDecodeRequest.TargetSampleRate
                * PlatformAudioChunkDecodeRequest.TargetChannels
                * PlatformAudioChunkDecodeRequest.TargetBitsPerSample / 8
            || wave.BlockAlign
                != PlatformAudioChunkDecodeRequest.TargetChannels
                * PlatformAudioChunkDecodeRequest.TargetBitsPerSample / 8
            || BinaryPrimitives.ReadUInt16LittleEndian(wave.FormatBytes.AsSpan(14, 2))
                != PlatformAudioChunkDecodeRequest.TargetBitsPerSample
            || wave.DataLength <= 0
            || wave.DataLength % wave.BlockAlign != 0)
        {
            throw new InvalidDataException("The platform decoder output is not canonical PCM WAV audio.");
        }

        var waveFrames = wave.DataLength / wave.BlockAlign;
        var maximumFrames = checked((long)Math.Ceiling(
            chunk.Duration.TotalSeconds * PlatformAudioChunkDecodeRequest.TargetSampleRate));
        var toleratedShortfallFrames = checked((long)Math.Ceiling(
            Math.Min(chunk.Duration.TotalSeconds, MaximumDecodedEndShortfall.TotalSeconds)
            * PlatformAudioChunkDecodeRequest.TargetSampleRate));
        var minimumFrames = Math.Max(1, maximumFrames - toleratedShortfallFrames);
        if (waveFrames < minimumFrames || waveFrames > maximumFrames + 1)
        {
            throw new InvalidDataException("The platform decoder WAV does not cover the requested time range.");
        }

        return waveFrames;
    }

    private static async ValueTask SliceWaveAsync(
        string sourcePath,
        string outputPath,
        AudioChunkDescriptor chunk,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var wave = await ReadWaveLayoutAsync(source, cancellationToken).ConfigureAwait(false);
        if (wave.BlockAlign <= 0 || wave.ByteRate <= 0)
        {
            throw new InvalidDataException("The WAV source has an invalid PCM layout.");
        }

        var startByte = AlignDown(
            checked((long)Math.Floor(chunk.Start.TotalSeconds * wave.ByteRate)),
            wave.BlockAlign);
        var endByte = AlignUp(
            checked((long)Math.Ceiling(chunk.End.TotalSeconds * wave.ByteRate)),
            wave.BlockAlign);
        startByte = Math.Clamp(startByte, 0, wave.DataLength);
        endByte = Math.Clamp(endByte, startByte, wave.DataLength);
        var dataLength = endByte - startByte;
        if (dataLength <= 0 || dataLength > uint.MaxValue)
        {
            throw new InvalidDataException("The requested WAV chunk has an invalid size.");
        }

        var fmtPadding = wave.FormatBytes.Length & 1;
        var dataPadding = (int)(dataLength & 1);
        var riffSize = checked(4L + 8L + wave.FormatBytes.Length + fmtPadding + 8L + dataLength + dataPadding);
        if (riffSize > uint.MaxValue)
        {
            throw new InvalidDataException("The requested WAV chunk exceeds the RIFF size limit.");
        }

        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WriteAsciiAsync(output, "RIFF", cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(output, checked((uint)riffSize), cancellationToken).ConfigureAwait(false);
        await WriteAsciiAsync(output, "WAVE", cancellationToken).ConfigureAwait(false);
        await WriteAsciiAsync(output, "fmt ", cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(output, checked((uint)wave.FormatBytes.Length), cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(wave.FormatBytes, cancellationToken).ConfigureAwait(false);
        if (fmtPadding != 0)
        {
            await output.WriteAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        }

        await WriteAsciiAsync(output, "data", cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(output, checked((uint)dataLength), cancellationToken).ConfigureAwait(false);
        source.Position = checked(wave.DataOffset + startByte);
        await CopyExactAsync(source, output, dataLength, cancellationToken).ConfigureAwait(false);
        if (dataPadding != 0)
        {
            await output.WriteAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WaveLayout> ReadWaveLayoutAsync(
        FileStream source,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        await ReadExactAsync(source, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("The source is not a RIFF/WAVE artifact.");
        }

        byte[]? formatBytes = null;
        long? dataOffset = null;
        long? dataLength = null;
        var chunkHeader = new byte[8];
        while (source.Position + chunkHeader.Length <= source.Length)
        {
            await ReadExactAsync(source, chunkHeader, cancellationToken).ConfigureAwait(false);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            var payloadOffset = source.Position;
            if (payloadOffset + chunkLength > source.Length)
            {
                throw new InvalidDataException("A WAV chunk extends past the end of the source.");
            }

            if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (chunkLength is < 16 or > 65_536)
                {
                    throw new InvalidDataException("The WAV format chunk has an invalid size.");
                }

                formatBytes = new byte[checked((int)chunkLength)];
                await ReadExactAsync(source, formatBytes, cancellationToken).ConfigureAwait(false);
            }
            else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                dataOffset = payloadOffset;
                dataLength = chunkLength;
                source.Position = checked(payloadOffset + chunkLength);
            }
            else
            {
                source.Position = checked(payloadOffset + chunkLength);
            }

            if ((chunkLength & 1) != 0 && source.Position < source.Length)
            {
                source.Position++;
            }

            if (formatBytes is not null && dataOffset is not null)
            {
                break;
            }
        }

        if (formatBytes is null || dataOffset is null || dataLength is null)
        {
            throw new InvalidDataException("The WAV source is missing its format or data chunk.");
        }

        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.AsSpan(8, 4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(12, 2));
        return new WaveLayout(formatBytes, dataOffset.Value, dataLength.Value, byteRate, blockAlign);
    }

    private static async ValueTask SliceMp3Async(
        string sourcePath,
        string outputPath,
        AudioChunkDescriptor chunk,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await SkipId3v2Async(source, cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var timeline = TimeSpan.Zero;
        var header = new byte[4];
        var copiedFrames = 0;
        while (source.Position + header.Length <= source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidatePosition = source.Position;
            await ReadExactAsync(source, header, cancellationToken).ConfigureAwait(false);
            if (!TryParseMp3Frame(header, out var frame))
            {
                source.Position = candidatePosition + 1;
                continue;
            }

            if (candidatePosition + frame.Length > source.Length)
            {
                break;
            }

            var frameStart = timeline;
            var frameEnd = timeline + frame.Duration;
            var include = frameEnd > chunk.Start && frameStart < chunk.End;
            if (include)
            {
                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await CopyExactAsync(source, output, frame.Length - header.Length, cancellationToken)
                    .ConfigureAwait(false);
                copiedFrames++;
            }
            else
            {
                source.Position = candidatePosition + frame.Length;
            }

            timeline = frameEnd;
            if (frameStart >= chunk.End)
            {
                break;
            }
        }

        if (copiedFrames == 0)
        {
            throw new InvalidDataException("No complete MP3 frames intersect the requested chunk range.");
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SkipId3v2Async(FileStream source, CancellationToken cancellationToken)
    {
        if (source.Length < 10)
        {
            return;
        }

        var header = new byte[10];
        await ReadExactAsync(source, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            source.Position = 0;
            return;
        }

        if ((header[6] | header[7] | header[8] | header[9]) >= 0x80)
        {
            throw new InvalidDataException("The MP3 ID3 header has an invalid synchsafe size.");
        }

        var tagSize = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
        var footerSize = (header[5] & 0x10) != 0 ? 10 : 0;
        var next = checked(10L + tagSize + footerSize);
        if (next > source.Length)
        {
            throw new InvalidDataException("The MP3 ID3 tag extends past the source file.");
        }

        source.Position = next;
    }

    private static bool TryParseMp3Frame(ReadOnlySpan<byte> header, out Mp3Frame frame)
    {
        frame = default;
        if (header.Length < 4 || header[0] != 0xff || (header[1] & 0xe0) != 0xe0)
        {
            return false;
        }

        var versionBits = (header[1] >> 3) & 0x03;
        var layerBits = (header[1] >> 1) & 0x03;
        var bitrateIndex = (header[2] >> 4) & 0x0f;
        var sampleRateIndex = (header[2] >> 2) & 0x03;
        var padding = (header[2] >> 1) & 0x01;
        if (versionBits == 1 || layerBits != 1 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
        {
            return false;
        }

        var mpeg1 = versionBits == 3;
        var baseSampleRate = sampleRateIndex switch
        {
            0 => 44_100,
            1 => 48_000,
            2 => 32_000,
            _ => 0
        };
        var sampleRate = versionBits switch
        {
            3 => baseSampleRate,
            2 => baseSampleRate / 2,
            0 => baseSampleRate / 4,
            _ => 0
        };
        var bitrateKbps = mpeg1
            ? Mpeg1Layer3Bitrates[bitrateIndex]
            : Mpeg2Layer3Bitrates[bitrateIndex];
        if (sampleRate <= 0 || bitrateKbps <= 0)
        {
            return false;
        }

        var samplesPerFrame = mpeg1 ? 1152 : 576;
        var coefficient = mpeg1 ? 144 : 72;
        var frameLength = coefficient * bitrateKbps * 1000 / sampleRate + padding;
        if (frameLength < 24)
        {
            return false;
        }

        frame = new Mp3Frame(
            frameLength,
            TimeSpan.FromSeconds((double)samplesPerFrame / sampleRate));
        return true;
    }

    private static readonly int[] Mpeg1Layer3Bitrates =
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];

    private static readonly int[] Mpeg2Layer3Bitrates =
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    private static async ValueTask<MaterializedAudioChunk> DescribeAsync(
        string path,
        string format,
        bool isTemporary,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return new MaterializedAudioChunk(path, format, hash, stream.Length, isTemporary);
    }

    private static async ValueTask CopyExactAsync(
        Stream source,
        Stream destination,
        long byteCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        var remaining = byteCount;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The audio source ended before the requested chunk was copied.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async ValueTask ReadExactAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The audio source ended unexpectedly.");
            }

            offset += read;
        }
    }

    private static ValueTask WriteAsciiAsync(
        Stream destination,
        string value,
        CancellationToken cancellationToken) =>
        destination.WriteAsync(System.Text.Encoding.ASCII.GetBytes(value), cancellationToken);

    private static ValueTask WriteUInt32Async(
        Stream destination,
        uint value,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return destination.WriteAsync(bytes, cancellationToken);
    }

    private static long AlignDown(long value, int alignment) => value / alignment * alignment;

    private static long AlignUp(long value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static string NormalizeFormat(string path)
    {
        var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(format) ? "bin" : format;
    }

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

    private readonly record struct WaveLayout(
        byte[] FormatBytes,
        long DataOffset,
        long DataLength,
        long ByteRate,
        int BlockAlign);

    private readonly record struct Mp3Frame(int Length, TimeSpan Duration);

    private readonly record struct SourceFileState(long SizeBytes, long LastWriteTimeUtcTicks);

    private readonly record struct VerifiedSourceSnapshot(
        long SizeBytes,
        long LastWriteTimeUtcTicks,
        string ExpectedSha256,
        long Sequence);
}

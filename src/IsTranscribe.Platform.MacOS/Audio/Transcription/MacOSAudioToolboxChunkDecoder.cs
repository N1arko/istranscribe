using System.Buffers.Binary;
using System.Runtime.InteropServices;
using IsTranscribe.Application.Transcription;
using Microsoft.Win32.SafeHandles;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Uses the macOS AudioToolbox decoder and sample converter for bounded transcription PCM.
/// </summary>
/// <remarks>
/// The product runtime calls the framework in-process through this platform assembly. No codec
/// executable, PATH lookup or native library load is present in Desktop or Application.
///
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#dependencies
/// </remarks>
public sealed class MacOSAudioToolboxChunkDecoder : IPlatformAudioChunkDecoder
{
    private const string AudioToolboxLibrary =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundationLibrary =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint AudioFormatLinearPcm = 0x6c70636d; // 'lpcm'
    private const uint AudioFormatFlagIsSignedInteger = 1U << 2;
    private const uint AudioFormatFlagIsPacked = 1U << 3;
    private const uint ExtAudioFilePropertyFileDataFormat = 0x66666d74; // 'ffmt'
    private const uint ExtAudioFilePropertyClientDataFormat = 0x63666d74; // 'cfmt'
    private const uint ExtAudioFilePropertyFileLengthFrames = 0x2366726d; // '#frm'
    private const uint AudioFileGlobalInfoReadableTypes = 0x61667266; // 'afrf'
    private const uint AudioFileGlobalInfoAvailableFormatIds = 0x666d6964; // 'fmid'
    private const uint AudioFileTypeMpegLayer3 = 0x4d504733; // 'MPG3'
    private const uint AudioFileTypeMpeg4Audio = 0x6d346166; // 'm4af'
    private const uint AudioFileTypeMpeg4 = 0x6d703466; // 'mp4f'
    private const uint AudioFileTypeFlac = 0x666c6163; // 'flac'
    private const uint AudioFileTypeOgg = 0x4f676766; // 'Oggf'
    private const uint AudioFileTypeWave = 0x57415645; // 'WAVE'
    private const uint AudioFormatMpegLayer3 = 0x2e6d7033; // '.mp3'
    private const uint AudioFormatFlac = 0x666c6163; // 'flac'
    private const uint AudioFormatOpus = 0x6f707573; // 'opus'
    private const uint AudioFormatVorbis = 0x766f7262; // 'vorb'
    private const int FramesPerRead = 4_096;
    private const int WaveHeaderLength = 44;
    private static readonly Lazy<AudioToolboxDecodeCapabilities> SystemCapabilities =
        new(ProbeSystemCapabilities, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool CanDecode(string sourceFormat) =>
        OperatingSystem.IsMacOSVersionAtLeast(14, 2)
        && !string.IsNullOrWhiteSpace(sourceFormat)
        && SystemCapabilities.Value.Supports(sourceFormat.Trim().TrimStart('.'));

    public async ValueTask<PlatformAudioChunkDecodeResult> DecodeAsync(
        PlatformAudioChunkDecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = request.Validate();
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            throw new PlatformNotSupportedException(
                "The AudioToolbox transcription decoder requires macOS 14.2 or newer.");
        }

        if (!CanDecode(validated.SourceFormat))
        {
            throw new NotSupportedException(
                $"AudioToolbox transcription decoding is unavailable for '{validated.SourceFormat}' audio.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var sourceLease = new FileStream(
            validated.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var sourceBefore = CaptureSourceState(validated.SourcePath);
        if (sourceLease.Length != sourceBefore.SizeBytes
            || sourceBefore.SizeBytes != validated.ExpectedSourceSizeBytes)
        {
            throw new InvalidDataException("The transcription source size no longer matches its job identity.");
        }

        var outputDirectory = Path.GetDirectoryName(validated.OutputWavePath)
            ?? throw new InvalidDataException("The decoded PCM output directory is unavailable.");
        Directory.CreateDirectory(outputDirectory);

        var completed = false;
        try
        {
            var result = await DecodeCoreAsync(validated, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAfter = CaptureSourceState(validated.SourcePath);
            if (sourceAfter != sourceBefore)
            {
                throw new InvalidDataException("The transcription source changed while AudioToolbox decoded it.");
            }

            completed = true;
            return result;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(validated.OutputWavePath);
            }
        }
    }

    private static async ValueTask<PlatformAudioChunkDecodeResult> DecodeCoreAsync(
        PlatformAudioChunkDecodeRequest request,
        CancellationToken cancellationToken)
    {
        using var url = CreateFileUrl(request.SourcePath);
        ThrowIfError(
            ExtAudioFileOpenUrl(url, out var audioFilePointer),
            "open_source");
        using var audioFile = new SafeExtAudioFileHandle(audioFilePointer);

        var sourceFormat = GetSourceFormat(audioFile);
        if (!double.IsFinite(sourceFormat.SampleRate)
            || sourceFormat.SampleRate <= 0
            || sourceFormat.ChannelsPerFrame == 0)
        {
            throw new InvalidDataException("AudioToolbox returned an invalid source audio layout.");
        }

        var sourceLengthFrames = GetSourceLengthFrames(audioFile);
        if (sourceLengthFrames <= 0)
        {
            throw new InvalidDataException("AudioToolbox reported an empty source audio stream.");
        }

        var actualSourceDuration = TimeSpan.FromSeconds(sourceLengthFrames / sourceFormat.SampleRate);
        if (request.Start >= actualSourceDuration)
        {
            throw new InvalidDataException("The requested transcription range starts past readable audio.");
        }

        var readableEnd = request.End <= actualSourceDuration
            ? request.End
            : actualSourceDuration;
        var readableDuration = readableEnd - request.Start;
        if (readableDuration <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The requested transcription range contains no readable audio.");
        }

        var clientFormat = CreateClientFormat();
        ThrowIfError(
            ExtAudioFileSetProperty(
                audioFile,
                ExtAudioFilePropertyClientDataFormat,
                checked((uint)Marshal.SizeOf<AudioStreamBasicDescription>()),
                ref clientFormat),
            "set_client_format");

        var sourceStartFrame = Math.Clamp(
            checked((long)Math.Floor(request.Start.TotalSeconds * sourceFormat.SampleRate)),
            0,
            sourceLengthFrames - 1);
        ThrowIfError(ExtAudioFileSeek(audioFile, sourceStartFrame), "seek_range");

        var requestedOutputFrames = checked((long)Math.Ceiling(
            readableDuration.TotalSeconds * PlatformAudioChunkDecodeRequest.TargetSampleRate));
        if (requestedOutputFrames <= 0)
        {
            throw new InvalidDataException("The requested transcription range is shorter than one PCM frame.");
        }

        await using var output = new FileStream(
            request.OutputWavePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(new byte[WaveHeaderLength], cancellationToken).ConfigureAwait(false);

        var pcm = new byte[FramesPerRead * sizeof(short)];
        var pinnedPcm = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        long writtenFrames = 0;
        try
        {
            while (writtenFrames < requestedOutputFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var framesToRead = checked((uint)Math.Min(
                    FramesPerRead,
                    requestedOutputFrames - writtenFrames));
                var bufferList = new AudioBufferList
                {
                    NumberBuffers = 1,
                    Buffer = new AudioBuffer
                    {
                        NumberChannels = PlatformAudioChunkDecodeRequest.TargetChannels,
                        DataByteSize = checked(framesToRead * sizeof(short)),
                        Data = pinnedPcm.AddrOfPinnedObject()
                    }
                };
                var framesRead = framesToRead;
                ThrowIfError(
                    ExtAudioFileRead(audioFile, ref framesRead, ref bufferList),
                    "read_range");
                if (framesRead == 0)
                {
                    break;
                }

                if (framesRead > framesToRead
                    || bufferList.Buffer.DataByteSize < framesRead * sizeof(short))
                {
                    throw new InvalidDataException("AudioToolbox returned an invalid PCM buffer length.");
                }

                var byteCount = checked((int)framesRead * sizeof(short));
                await output.WriteAsync(pcm.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
                writtenFrames = checked(writtenFrames + framesRead);
            }
        }
        finally
        {
            pinnedPcm.Free();
        }

        if (writtenFrames <= 0)
        {
            throw new InvalidDataException("AudioToolbox produced no PCM samples for the requested range.");
        }

        var waveHeader = CreateWaveHeader(writtenFrames);
        output.Position = 0;
        await output.WriteAsync(waveHeader, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);

        return new PlatformAudioChunkDecodeResult(
            request.OutputWavePath,
            PlatformAudioChunkDecodeRequest.TargetSampleRate,
            PlatformAudioChunkDecodeRequest.TargetChannels,
            PlatformAudioChunkDecodeRequest.TargetBitsPerSample,
            writtenFrames,
            request.Start,
            readableEnd);
    }

    private static SafeCoreFoundationHandle CreateFileUrl(string sourcePath)
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(sourcePath);
        var url = CoreFoundationUrlCreateFromFileSystemRepresentation(
            IntPtr.Zero,
            pathBytes,
            pathBytes.Length,
            isDirectory: false);
        if (url == IntPtr.Zero)
        {
            throw new InvalidDataException("CoreFoundation could not represent the source audio path.");
        }

        return new SafeCoreFoundationHandle(url);
    }

    private static AudioStreamBasicDescription GetSourceFormat(SafeExtAudioFileHandle audioFile)
    {
        var size = checked((uint)Marshal.SizeOf<AudioStreamBasicDescription>());
        ThrowIfError(
            ExtAudioFileGetProperty(
                audioFile,
                ExtAudioFilePropertyFileDataFormat,
                ref size,
                out AudioStreamBasicDescription format),
            "read_source_format");
        if (size != Marshal.SizeOf<AudioStreamBasicDescription>())
        {
            throw new InvalidDataException("AudioToolbox returned an unexpected source format size.");
        }

        return format;
    }

    private static long GetSourceLengthFrames(SafeExtAudioFileHandle audioFile)
    {
        var size = checked((uint)sizeof(long));
        ThrowIfError(
            ExtAudioFileGetInt64Property(
                audioFile,
                ExtAudioFilePropertyFileLengthFrames,
                ref size,
                out var frames),
            "read_source_length");
        if (size != sizeof(long))
        {
            throw new InvalidDataException("AudioToolbox returned an unexpected source length size.");
        }

        return frames;
    }

    private static AudioStreamBasicDescription CreateClientFormat() =>
        new()
        {
            SampleRate = PlatformAudioChunkDecodeRequest.TargetSampleRate,
            FormatId = AudioFormatLinearPcm,
            FormatFlags = AudioFormatFlagIsSignedInteger | AudioFormatFlagIsPacked,
            BytesPerPacket = sizeof(short),
            FramesPerPacket = 1,
            BytesPerFrame = sizeof(short),
            ChannelsPerFrame = PlatformAudioChunkDecodeRequest.TargetChannels,
            BitsPerChannel = PlatformAudioChunkDecodeRequest.TargetBitsPerSample
        };

    private static byte[] CreateWaveHeader(long sampleFrames)
    {
        var dataLength = checked(sampleFrames * sizeof(short));
        if (dataLength > uint.MaxValue - 36L)
        {
            throw new InvalidDataException("The decoded transcription range exceeds the WAV size limit.");
        }

        var header = new byte[WaveHeaderLength];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), checked((uint)(36 + dataLength)));
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
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), checked((uint)dataLength));
        return header;
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

    private static AudioToolboxDecodeCapabilities ProbeSystemCapabilities()
    {
        try
        {
            var status = AudioFileGetGlobalInfoSize(
                AudioFileGlobalInfoReadableTypes,
                specifierSize: 0,
                IntPtr.Zero,
                out var readableTypeBytes);
            if (status != 0 || readableTypeBytes == 0 || readableTypeBytes % sizeof(uint) != 0)
            {
                return AudioToolboxDecodeCapabilities.Empty;
            }

            var readableTypes = new uint[checked((int)(readableTypeBytes / sizeof(uint)))];
            status = AudioFileGetGlobalInfo(
                AudioFileGlobalInfoReadableTypes,
                specifierSize: 0,
                IntPtr.Zero,
                ref readableTypeBytes,
                readableTypes);
            if (status != 0)
            {
                return AudioToolboxDecodeCapabilities.Empty;
            }

            var formats = new Dictionary<uint, IReadOnlySet<uint>>();
            foreach (var fileType in readableTypes.Distinct())
            {
                var specifier = fileType;
                status = AudioFileGetGlobalInfoSizeWithSpecifier(
                    AudioFileGlobalInfoAvailableFormatIds,
                    sizeof(uint),
                    ref specifier,
                    out var formatBytes);
                if (status != 0 || formatBytes == 0 || formatBytes % sizeof(uint) != 0)
                {
                    continue;
                }

                var formatIds = new uint[checked((int)(formatBytes / sizeof(uint)))];
                status = AudioFileGetGlobalInfoWithSpecifier(
                    AudioFileGlobalInfoAvailableFormatIds,
                    sizeof(uint),
                    ref specifier,
                    ref formatBytes,
                    formatIds);
                if (status == 0)
                {
                    formats[fileType] = formatIds.ToHashSet();
                }
            }

            return new AudioToolboxDecodeCapabilities(readableTypes.ToHashSet(), formats);
        }
        catch (Exception)
        {
            return AudioToolboxDecodeCapabilities.Empty;
        }
    }

    private static void ThrowIfError(int status, string operation)
    {
        if (status != 0)
        {
            throw new InvalidDataException(
                $"AudioToolbox failed during '{operation}' with OSStatus {status}.");
        }
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

    [DllImport(CoreFoundationLibrary, EntryPoint = "CFURLCreateFromFileSystemRepresentation")]
    private static extern IntPtr CoreFoundationUrlCreateFromFileSystemRepresentation(
        IntPtr allocator,
        byte[] buffer,
        nint bufferLength,
        [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [DllImport(CoreFoundationLibrary, EntryPoint = "CFRelease")]
    private static extern void CoreFoundationRelease(IntPtr value);

    [DllImport(AudioToolboxLibrary, EntryPoint = "ExtAudioFileOpenURL")]
    private static extern int ExtAudioFileOpenUrl(
        SafeCoreFoundationHandle url,
        out IntPtr audioFile);

    [DllImport(AudioToolboxLibrary, EntryPoint = "ExtAudioFileDispose")]
    private static extern int ExtAudioFileDispose(IntPtr audioFile);

    [DllImport(AudioToolboxLibrary, EntryPoint = "ExtAudioFileGetProperty")]
    private static extern int ExtAudioFileGetProperty(
        SafeExtAudioFileHandle audioFile,
        uint propertyId,
        ref uint propertyDataSize,
        out AudioStreamBasicDescription propertyData);

    [DllImport(AudioToolboxLibrary, EntryPoint = "ExtAudioFileGetProperty")]
    private static extern int ExtAudioFileGetInt64Property(
        SafeExtAudioFileHandle audioFile,
        uint propertyId,
        ref uint propertyDataSize,
        out long propertyData);

    [DllImport(AudioToolboxLibrary, EntryPoint = "ExtAudioFileSetProperty")]
    private static extern int ExtAudioFileSetProperty(
        SafeExtAudioFileHandle audioFile,
        uint propertyId,
        uint propertyDataSize,
        ref AudioStreamBasicDescription propertyData);

    [DllImport(AudioToolboxLibrary, EntryPoint = "ExtAudioFileSeek")]
    private static extern int ExtAudioFileSeek(SafeExtAudioFileHandle audioFile, long frameOffset);

    [DllImport(AudioToolboxLibrary, EntryPoint = "ExtAudioFileRead")]
    private static extern int ExtAudioFileRead(
        SafeExtAudioFileHandle audioFile,
        ref uint numberFrames,
        ref AudioBufferList data);

    [DllImport(AudioToolboxLibrary, EntryPoint = "AudioFileGetGlobalInfoSize")]
    private static extern int AudioFileGetGlobalInfoSize(
        uint propertyId,
        uint specifierSize,
        IntPtr specifier,
        out uint dataSize);

    [DllImport(AudioToolboxLibrary, EntryPoint = "AudioFileGetGlobalInfo")]
    private static extern int AudioFileGetGlobalInfo(
        uint propertyId,
        uint specifierSize,
        IntPtr specifier,
        ref uint dataSize,
        [Out] uint[] data);

    [DllImport(AudioToolboxLibrary, EntryPoint = "AudioFileGetGlobalInfoSize")]
    private static extern int AudioFileGetGlobalInfoSizeWithSpecifier(
        uint propertyId,
        uint specifierSize,
        ref uint specifier,
        out uint dataSize);

    [DllImport(AudioToolboxLibrary, EntryPoint = "AudioFileGetGlobalInfo")]
    private static extern int AudioFileGetGlobalInfoWithSpecifier(
        uint propertyId,
        uint specifierSize,
        ref uint specifier,
        ref uint dataSize,
        [Out] uint[] data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer;
    }

    private sealed class SafeCoreFoundationHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeCoreFoundationHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            CoreFoundationRelease(handle);
            return true;
        }
    }

    private sealed class SafeExtAudioFileHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeExtAudioFileHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => ExtAudioFileDispose(handle) == 0;
    }

    private readonly record struct SourceFileState(long SizeBytes, long LastWriteTimeUtcTicks);

    private sealed record AudioToolboxDecodeCapabilities(
        IReadOnlySet<uint> ReadableTypes,
        IReadOnlyDictionary<uint, IReadOnlySet<uint>> Formats)
    {
        public static AudioToolboxDecodeCapabilities Empty { get; } =
            new(new HashSet<uint>(), new Dictionary<uint, IReadOnlySet<uint>>());

        public bool Supports(string extension) => extension.ToLowerInvariant() switch
        {
            "mp3" => Supports(AudioFileTypeMpegLayer3, AudioFormatMpegLayer3),
            "m4a" => Supports(AudioFileTypeMpeg4Audio),
            "mp4" => Supports(AudioFileTypeMpeg4),
            "flac" => Supports(AudioFileTypeFlac, AudioFormatFlac),
            "ogg" or "oga" or "opus" => SupportsAny(
                AudioFileTypeOgg,
                AudioFormatOpus,
                AudioFormatVorbis,
                AudioFormatFlac),
            "wav" or "wave" => Supports(AudioFileTypeWave),
            _ => false
        };

        private bool Supports(uint fileType, uint? requiredFormat = null) =>
            ReadableTypes.Contains(fileType)
            && Formats.TryGetValue(fileType, out var formats)
            && formats.Count > 0
            && (requiredFormat is null || formats.Contains(requiredFormat.Value));

        private bool SupportsAny(uint fileType, params uint[] requiredFormats) =>
            ReadableTypes.Contains(fileType)
            && Formats.TryGetValue(fileType, out var formats)
            && requiredFormats.Any(formats.Contains);
    }
}

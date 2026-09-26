using System.Runtime.InteropServices;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// AudioToolbox-backed MP3 encoding and verification for the fixed macOS speech preset.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#recording
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.format
/// </remarks>
public sealed class MacOSMp3Encoder
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int Bitrate = 128_000;
    private const uint Mp3FormatId = 0x2e6d7033; // '.mp3'
    private const string LibraryName = "istranscribe_audio";

    public async ValueTask<MacOSAudioArtifactInfo> EncodeAsync(
        string sourceWavePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWavePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourceWavePath))
        {
            throw new FileNotFoundException("The source wave artifact does not exist.", sourceWavePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await Task.Run(
            () => EncodeWave(sourceWavePath, destinationPath, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        try
        {
            return Probe(destinationPath);
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    private static void EncodeWave(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var encoder = LameNative.Initialize();
        var destinationCreated = false;
        try
        {
            using var source = OpenPcmWave(sourcePath, out var dataBytes);
            using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough);
            destinationCreated = true;
            var pcm = new short[2 * 4_096];
            var encoded = new byte[checked((int)(1.25 * 4_096) + 7_200)];
            var remaining = dataBytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var samplesToRead = Math.Min(pcm.Length, remaining / sizeof(short));
                var bytes = MemoryMarshal.AsBytes(pcm.AsSpan(0, samplesToRead));
                source.ReadExactly(bytes);
                remaining -= bytes.Length;
                var written = LameNative.EncodeInterleaved(
                    encoder,
                    pcm,
                    samplesToRead / Channels,
                    encoded,
                    encoded.Length);
                EnsureLameSucceeded(written);
                destination.Write(encoded, 0, written);
            }

            var flushed = LameNative.Flush(encoder, encoded, encoded.Length);
            EnsureLameSucceeded(flushed);
            destination.Write(encoded, 0, flushed);
            destination.Flush(flushToDisk: true);
        }
        catch
        {
            if (destinationCreated)
            {
                File.Delete(destinationPath);
            }

            throw;
        }
        finally
        {
            LameNative.Close(encoder);
        }
    }

    private static FileStream OpenPcmWave(string path, out int dataBytes)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                throw new InvalidDataException("The source is not a RIFF wave file.");
            }

            _ = reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                throw new InvalidDataException("The source is not a WAVE file.");
            }

            var validFormat = false;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunk = new string(reader.ReadChars(4));
                var length = reader.ReadInt32();
                if (length < 0 || stream.Position + length > stream.Length)
                {
                    throw new InvalidDataException("The wave file contains an invalid chunk.");
                }

                if (chunk == "fmt ")
                {
                    var format = reader.ReadUInt16();
                    var channels = reader.ReadUInt16();
                    var sampleRate = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    var bits = reader.ReadUInt16();
                    validFormat = format == 1 && channels == Channels && sampleRate == SampleRate && bits == 16;
                    stream.Position += length - 16;
                }
                else if (chunk == "data")
                {
                    if (!validFormat || length % (Channels * sizeof(short)) != 0)
                    {
                        throw new InvalidDataException("The source must be 48 kHz stereo PCM16.");
                    }

                    dataBytes = length;
                    return stream;
                }
                else
                {
                    stream.Position += length;
                }

                if ((length & 1) != 0)
                {
                    stream.Position++;
                }
            }

            throw new InvalidDataException("The wave file has no readable data chunk.");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureLameSucceeded(int result)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"LAME encoding failed with code {result}.");
        }
    }

    public MacOSAudioArtifactInfo Probe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var status = NativeMethods.ProbeAudio(
            path,
            out var sampleRate,
            out var channels,
            out var durationSeconds,
            out var formatId,
            out var bitrate);
        if (status != 0)
        {
            throw new MacOSAudioException(status);
        }

        var info = new MacOSAudioArtifactInfo(
            path,
            checked((int)Math.Round(sampleRate)),
            checked((int)channels),
            checked((int)bitrate),
            TimeSpan.FromSeconds(durationSeconds),
            new FileInfo(path).Length);
        if (formatId != Mp3FormatId
            || info.SampleRate != SampleRate
            || info.Channels != Channels
            || Math.Abs(info.Bitrate - Bitrate) > 2_000
            || info.Duration <= TimeSpan.Zero
            || info.Bytes <= 0)
        {
            throw new InvalidDataException("The encoded artifact does not satisfy the macOS MP3 contract.");
        }

        return info;
    }

    internal double MeasureTone(string path, double frequency)
    {
        var status = NativeMethods.MeasureTone(path, frequency, out var amplitude);
        if (status != 0)
        {
            throw new MacOSAudioException(status);
        }

        return amplitude;
    }

    private static class NativeMethods
    {
        [DllImport(LibraryName, EntryPoint = "ist_probe_audio", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ProbeAudio(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            out double sampleRate,
            out uint channels,
            out double durationSeconds,
            out uint formatId,
            out uint bitrate);

        [DllImport(LibraryName, EntryPoint = "ist_measure_tone", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int MeasureTone(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            double frequency,
            out double amplitude);
    }

    private static class LameNative
    {
        private const string LameLibrary = "libmp3lame.0.dylib";

        internal static nint Initialize()
        {
            var encoder = Init();
            if (encoder == nint.Zero)
            {
                throw new InvalidOperationException("LAME encoder initialization failed.");
            }

            if (SetInputSampleRate(encoder, SampleRate) != 0
                || SetChannels(encoder, Channels) != 0
                || SetBitrate(encoder, Bitrate / 1_000) != 0
                || SetMode(encoder, 1) != 0
                || SetQuality(encoder, 2) != 0
                || InitializeParameters(encoder) != 0)
            {
                Close(encoder);
                throw new InvalidOperationException("LAME rejected the fixed macOS MP3 preset.");
            }

            return encoder;
        }

        [DllImport(LameLibrary, EntryPoint = "lame_init", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint Init();

        [DllImport(LameLibrary, EntryPoint = "lame_set_in_samplerate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetInputSampleRate(nint encoder, int sampleRate);

        [DllImport(LameLibrary, EntryPoint = "lame_set_num_channels", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetChannels(nint encoder, int channels);

        [DllImport(LameLibrary, EntryPoint = "lame_set_brate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetBitrate(nint encoder, int bitrateKbps);

        [DllImport(LameLibrary, EntryPoint = "lame_set_mode", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetMode(nint encoder, int mode);

        [DllImport(LameLibrary, EntryPoint = "lame_set_quality", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetQuality(nint encoder, int quality);

        [DllImport(LameLibrary, EntryPoint = "lame_init_params", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InitializeParameters(nint encoder);

        [DllImport(LameLibrary, EntryPoint = "lame_encode_buffer_interleaved", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeInterleaved(
            nint encoder,
            [In] short[] pcm,
            int samplesPerChannel,
            [Out] byte[] encoded,
            int encodedCapacity);

        [DllImport(LameLibrary, EntryPoint = "lame_encode_flush", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Flush(nint encoder, [Out] byte[] encoded, int encodedCapacity);

        [DllImport(LameLibrary, EntryPoint = "lame_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(nint encoder);
    }
}

public sealed record MacOSAudioArtifactInfo(
    string Path,
    int SampleRate,
    int Channels,
    int Bitrate,
    TimeSpan Duration,
    long Bytes);

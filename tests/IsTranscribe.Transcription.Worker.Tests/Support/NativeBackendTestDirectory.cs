using System.Buffers.Binary;
using System.Security.Cryptography;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Worker.Runtime.Native;

namespace IsTranscribe.Transcription.Worker.Tests.Support;

internal sealed class NativeBackendTestDirectory : IDisposable
{
    public NativeBackendTestDirectory()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "istranscribe-native-tests",
            Guid.NewGuid().ToString("N"));
        NativeDirectory = Path.Combine(Root, "native");
        Directory.CreateDirectory(NativeDirectory);
        ModelPath = Path.Combine(Root, "ggml-small.bin");
        File.WriteAllBytes(ModelPath, [1, 2, 3, 4]);
        ModelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ModelPath)))
            .ToLowerInvariant();
        InputPath = Path.Combine(Root, "normalized.wav");
        WriteWave(InputPath, 16_000, encoding: 1, bitsPerSample: 16);
        InputSha256 = HashFile(InputPath);
    }

    public string Root { get; }

    public string NativeDirectory { get; }

    public string ModelPath { get; }

    public string ModelSha256 { get; }

    public string InputPath { get; }

    public string InputSha256 { get; private set; }

    public string AddNativeFile(string fileName)
    {
        var path = Path.Combine(NativeDirectory, fileName);
        File.WriteAllBytes(path, [0x7f]);
        return path;
    }

    public WorkerProbePayload Probe(string backend = "cpu") => new(
        ModelPath,
        ModelSha256,
        backend,
        MaximumThreads: 4);

    public WorkerStartPayload Start(string backend = "cpu") => new(
        InputPath,
        InputSha256,
        ModelPath,
        ModelSha256,
        "auto",
        backend,
        StartMilliseconds: 5_000,
        EndMilliseconds: 6_000,
        MaximumThreads: 4);

    public void WriteFloatWave(int sampleCount = 16_000)
    {
        WriteWave(InputPath, sampleCount, encoding: 3, bitsPerSample: 32);
        InputSha256 = HashFile(InputPath);
    }

    public void WritePcmWave(int bitsPerSample, int sampleCount = 16_000)
    {
        WriteWave(
            InputPath,
            sampleCount,
            encoding: 1,
            bitsPerSample: checked((ushort)bitsPerSample));
        InputSha256 = HashFile(InputPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static void WriteWave(
        string path,
        int sampleCount,
        ushort encoding,
        ushort bitsPerSample)
    {
        var bytesPerSample = bitsPerSample / 8;
        var dataLength = checked(sampleCount * bytesPerSample);
        var bytes = new byte[checked(44 + dataLength)];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 8)));
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), encoding);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), NativeWhisperAbi.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(28, 4),
            checked(NativeWhisperAbi.SampleRate * (uint)bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), checked((ushort)bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(34, 2),
            bitsPerSample);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), checked((uint)dataLength));

        if (encoding == 3)
        {
            for (var index = 0; index < sampleCount; index++)
            {
                var sample = (float)(Math.Sin(index / 20d) * 0.25d);
                BinaryPrimitives.WriteInt32LittleEndian(
                    bytes.AsSpan(44 + index * 4, 4),
                    BitConverter.SingleToInt32Bits(sample));
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static string HashFile(string path) => Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(path)))
        .ToLowerInvariant();
}

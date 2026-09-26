using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using IsTranscribe.Core.Audio;

namespace IsTranscribe.Application.Transcription;

public sealed record TranscriptionSourceTrack(string Role, string Path, string Sha256, long SizeBytes, double DurationSeconds, double OffsetSeconds);

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#retention
public sealed record TranscriptionSourceHandoff(int Version, Guid SessionId, IReadOnlyList<TranscriptionSourceTrack> Tracks)
{
    public const string FileName = "transcription-sources.v1.json";
    public static bool Exists(string? root) => !string.IsNullOrEmpty(root) && File.Exists(System.IO.Path.Combine(root, FileName));

    public static void Create(string root, Guid sessionId, IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts)
    {
        if (Exists(root)) { _ = Read(root, sessionId); return; }
        var prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var tracks = new List<TranscriptionSourceTrack>();
        foreach (var artifact in artifacts.Where(static item => item.Kind is AudioCaptureArtifactKind.Microphone or AudioCaptureArtifactKind.Output)
                     .OrderBy(static item => item.RelativeStartOffset).ThenBy(static item => item.Kind))
        {
            var path = System.IO.Path.GetFullPath(artifact.Path);
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || new FileInfo(path).LinkTarget is not null)
                throw new InvalidDataException("transcription_source_path_invalid");
            using var stream = File.OpenRead(path);
            var duration = ReadWaveDuration(stream);
            stream.Position = 0;
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            tracks.Add(new TranscriptionSourceTrack(artifact.Kind == AudioCaptureArtifactKind.Microphone ? "microphone" : "system_output",
                path, hash, stream.Length, duration, artifact.RelativeStartOffset.TotalSeconds));
        }
        if (tracks.Count == 0) throw new InvalidDataException("transcription_sources_missing");
        var document = new TranscriptionSourceHandoff(1, sessionId, tracks);
        var target = System.IO.Path.Combine(root, FileName);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, document);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static TranscriptionSourceHandoff Read(string root, Guid sessionId)
    {
        var path = System.IO.Path.Combine(root, FileName);
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("transcription_handoff_invalid");
        var handoff = JsonSerializer.Deserialize<TranscriptionSourceHandoff>(stream)
            ?? throw new InvalidDataException("transcription_handoff_invalid");
        var prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (handoff.Version != 1 || handoff.SessionId != sessionId || handoff.Tracks.Count is 0 or > 1024
            || handoff.Tracks.Any(track => track.Role is not ("microphone" or "system_output")
                || !System.IO.Path.GetFullPath(track.Path).StartsWith(prefix, StringComparison.Ordinal)
                || track.Sha256.Length != 64 || track.Sha256.Any(static c => !Uri.IsHexDigit(c))
                || track.SizeBytes <= 0 || !double.IsFinite(track.DurationSeconds) || track.DurationSeconds <= 0
                || !double.IsFinite(track.OffsetSeconds) || track.OffsetSeconds < 0))
            throw new InvalidDataException("transcription_handoff_invalid");
        return handoff;
    }

    private static double ReadWaveDuration(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("source_wav_invalid");
        Span<byte> chunk = stackalloc byte[8];
        uint byteRate = 0;
        long dataBytes = 0;
        Span<byte> format = stackalloc byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var next = stream.Position + length + length % 2;
            if (next > stream.Length) throw new InvalidDataException("source_wav_invalid");
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (length < 16) throw new InvalidDataException("source_wav_invalid");
                stream.ReadExactly(format);
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
            }
            else if (chunk[..4].SequenceEqual("data"u8)) dataBytes += length;
            stream.Position = next;
        }
        if (byteRate == 0 || dataBytes <= 0) throw new InvalidDataException("source_wav_invalid");
        return (double)dataBytes / byteRate;
    }

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#retention
    public static bool ReleaseFiles(string root, string expectedRoot, Guid sessionId, string? primary)
    {
        var normalized = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (!string.Equals(normalized, System.IO.Path.GetFullPath(expectedRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.Ordinal)
            || new DirectoryInfo(normalized).LinkTarget is not null || !Exists(normalized)) return false;
        var prefix = normalized + System.IO.Path.DirectorySeparatorChar;
        // Conservative on case-sensitive volumes too: a primary alias must never
        // become a deletion target on a case-insensitive recording volume.
        if (primary is null || System.IO.Path.GetFullPath(primary).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        _ = Read(normalized, sessionId);
        // Validate the full tree before deleting any file. Never follow a redirected directory.
        var pending = new Stack<string>(); pending.Push(normalized);
        var files = new List<string>();
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("transcription_cleanup_link_invalid");
                if (entry is DirectoryInfo child) pending.Push(child.FullName);
                else if (entry.FullName != System.IO.Path.Combine(normalized, FileName)) files.Add(entry.FullName);
            }
        }
        foreach (var file in files) File.Delete(file);
        return true;
    }
}

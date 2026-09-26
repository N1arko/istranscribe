using System.Diagnostics;
using System.Text.Json;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Application.Transcription;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
public interface ISpeakerDiarizer
{
    ValueTask EnsureReadyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask<SpeakerDiarizationResult> ProcessAsync(MaterializedAudioChunk audio, string checkpointPath, CancellationToken cancellationToken);
}

public sealed class SpeakerProcessingException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// Each bounded chunk runs out of process; cancellation kills the entire child tree.
// Audio and voice vectors only travel through app-owned files, never diagnostic output.
public sealed class SpeakerDiarizationClient(string workerPath, DiarizationAssets assets) : ISpeakerDiarizer
{
    public async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!await assets.VerifyAsync(cancellationToken).ConfigureAwait(false))
            throw new SpeakerProcessingException("diarization_assets_missing");
        if (!File.Exists(workerPath)) throw new SpeakerProcessingException("diarization_runtime_missing");
    }
    public async ValueTask<SpeakerDiarizationResult> ProcessAsync(MaterializedAudioChunk audio, string checkpointPath, CancellationToken cancellationToken)
    {
        if (!await assets.VerifyAsync(cancellationToken).ConfigureAwait(false))
            throw new SpeakerProcessingException("diarization_assets_missing");
        if (File.Exists(checkpointPath))
            return await ReadAsync(checkpointPath, audio.Sha256, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(workerPath)) throw new SpeakerProcessingException("diarization_runtime_missing");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
        var temporary = checkpointPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        var start = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "--diarize", audio.Path, audio.Sha256, assets.Root, temporary,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var process = Process.Start(start) ?? throw new SpeakerProcessingException("diarization_failed");
            // Drain without retaining native output (may contain input paths).
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new SpeakerProcessingException("diarization_timeout");
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new SpeakerProcessingException("diarization_failed");
            var result = await ReadAsync(temporary, audio.Sha256, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, checkpointPath, overwrite: true);
            return result;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async ValueTask<SpeakerDiarizationResult> ReadAsync(string path, string inputHash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new SpeakerProcessingException("diarization_result_invalid");
        var result = await JsonSerializer.DeserializeAsync<SpeakerDiarizationResult>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new SpeakerProcessingException("diarization_result_invalid");
        result.Validate();
        if (result.InputSha256 != inputHash) throw new SpeakerProcessingException("input_changed");
        return result;
    }
}

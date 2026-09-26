using System.Diagnostics;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Host.Audio;

/// <summary>
/// Compresses WAV audio files to Opus/OGG using FFmpeg CLI.
/// </summary>
public sealed class AudioCompressor(BootstrapFileLogger logger)
{
    private const int OutputBitrateKbps = 128;
    private const int MicBitrateKbps = 64;
    private const string FfmpegExecutable = "ffmpeg";

    private readonly BootstrapFileLogger _logger = logger;

    /// <summary>
    /// Compresses a WAV file to Opus (.ogg). Returns the path of the compressed file, or null if FFmpeg is unavailable or fails.
    /// </summary>
    public async Task<string?> CompressToOpusAsync(string wavPath, bool isMicrophone, CancellationToken cancellationToken)
    {
        if (!File.Exists(wavPath))
        {
            return null;
        }

        var oggPath = Path.ChangeExtension(wavPath, ".ogg");
        var bitrate = isMicrophone ? MicBitrateKbps : OutputBitrateKbps;

        try
        {
            var arguments = isMicrophone
                ? $"-i \"{wavPath}\" -c:a libopus -b:a {bitrate}k -ac 1 -y \"{oggPath}\""
                : $"-i \"{wavPath}\" -c:a libopus -b:a {bitrate}k -y \"{oggPath}\"";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegExecutable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.Warning($"FFmpeg compression failed (exit {process.ExitCode}): {stderr.Substring(0, Math.Min(stderr.Length, 500))}");
                TryDeleteFile(oggPath);
                return null;
            }

            if (!File.Exists(oggPath) || new FileInfo(oggPath).Length == 0)
            {
                _logger.Warning("FFmpeg produced empty or missing output.");
                TryDeleteFile(oggPath);
                return null;
            }

            _logger.Info($"Compressed {Path.GetFileName(wavPath)} → {Path.GetFileName(oggPath)} ({new FileInfo(wavPath).Length / 1024}KB → {new FileInfo(oggPath).Length / 1024}KB).");
            return oggPath;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // FFmpeg not found on PATH.
            _logger.Warning("FFmpeg is not available. Audio compression skipped.");
            return null;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Audio compression failed for {Path.GetFileName(wavPath)}.");
            TryDeleteFile(oggPath);
            return null;
        }
    }

    /// <summary>
    /// Checks if FFmpeg is available on the system PATH.
    /// </summary>
    public static bool IsFfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = FfmpegExecutable,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });

            process?.WaitForExit(3000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
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
            // Best effort.
        }
    }
}

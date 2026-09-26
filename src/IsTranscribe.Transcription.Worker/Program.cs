using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;
using IsTranscribe.Transcription.Worker.Runtime;
using IsTranscribe.Transcription.Worker.Runtime.Native;

namespace IsTranscribe.Transcription.Worker;

// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == SpeakerDiarizationCommand.ModeArgument)
            return await SpeakerDiarizationCommand.RunAsync(args).ConfigureAwait(false);
        if (args.Length != 1
            || !string.Equals(args[0], WorkerProcessContract.ModeArgument, StringComparison.Ordinal))
        {
            return 64;
        }

        try
        {
            await using var pipes = WorkerChildPipeConnection.OpenFromEnvironment();
            await using var protocol = new WorkerFramedConnection(
                pipes.CommandInput,
                pipes.ResultOutput,
                ownsStreams: false);
            using var backend = NativeWhisperBackendFactory.CreateForAppBase(
                AppContext.BaseDirectory);
            var host = new LocalTranscriptionWorkerHost(
                protocol,
                backend,
                new ParentProcessMonitor());
            await host.RunAsync(CancellationToken.None);
            return 0;
        }
        catch (WorkerProtocolException)
        {
            return 65;
        }
        catch (Exception)
        {
            return 70;
        }
    }
}

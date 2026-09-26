using System.IO.Pipes;
using IsTranscribe.Transcription.Local.Worker;

namespace IsTranscribe.Transcription.Worker.Runtime;

/// <summary>
/// Opens the inherited child endpoints without accepting any job data through command-line
/// arguments.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public sealed class WorkerChildPipeConnection : IAsyncDisposable
{
    private WorkerChildPipeConnection(
        AnonymousPipeClientStream commandInput,
        AnonymousPipeClientStream resultOutput)
    {
        CommandInput = commandInput;
        ResultOutput = resultOutput;
    }

    public Stream CommandInput { get; }

    public Stream ResultOutput { get; }

    public static WorkerChildPipeConnection OpenFromEnvironment()
    {
        var commandHandle = Environment.GetEnvironmentVariable(
            WorkerProcessContract.CommandPipeEnvironmentVariable);
        var resultHandle = Environment.GetEnvironmentVariable(
            WorkerProcessContract.ResultPipeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(commandHandle) || string.IsNullOrWhiteSpace(resultHandle))
        {
            throw new InvalidOperationException("Worker pipe handles are unavailable.");
        }

        var command = new AnonymousPipeClientStream(PipeDirection.In, commandHandle);
        try
        {
            var result = new AnonymousPipeClientStream(PipeDirection.Out, resultHandle);
            return new WorkerChildPipeConnection(command, result);
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CommandInput.DisposeAsync();
        await ResultOutput.DisposeAsync();
    }
}

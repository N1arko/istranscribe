using System.Diagnostics;
using System.IO.Pipes;

namespace IsTranscribe.Transcription.Local.Worker;

public static class WorkerProcessContract
{
    public const string ModeArgument = "--local-transcription-worker";
    public const string CommandPipeEnvironmentVariable = "ISTRANSCRIBE_WORKER_COMMAND_PIPE";
    public const string ResultPipeEnvironmentVariable = "ISTRANSCRIBE_WORKER_RESULT_PIPE";
}

public interface IWorkerChildProcess : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void KillProcessTree();
}

public sealed record WorkerProcessConnection(
    Stream CommandOutput,
    Stream ResultInput,
    IWorkerChildProcess Process);

public interface IWorkerProcessLauncher
{
    WorkerProcessConnection Launch(string workerExecutablePath);
}

/// <summary>
/// Creates exactly two inherited anonymous protocol pipes. Job/model/input paths and
/// transcript text are sent later in framed messages and never become process arguments.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </summary>
public sealed class AnonymousPipeWorkerProcessLauncher : IWorkerProcessLauncher
{
    public WorkerProcessConnection Launch(string workerExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutablePath);
        var commandPipe = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        var resultPipe = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        Process? process = null;
        var processStarted = false;
        try
        {
            var startInfo = CreateStartInfo(
                workerExecutablePath,
                commandPipe.GetClientHandleAsString(),
                resultPipe.GetClientHandleAsString());
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Local transcription worker process did not start.");
            }

            processStarted = true;
            SetBackgroundPriority(process);
            commandPipe.DisposeLocalCopyOfClientHandle();
            resultPipe.DisposeLocalCopyOfClientHandle();
            return new WorkerProcessConnection(
                commandPipe,
                resultPipe,
                new SystemWorkerChildProcess(process));
        }
        catch
        {
            if (processStarted && process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process?.Dispose();
            commandPipe.Dispose();
            resultPipe.Dispose();
            throw;
        }
    }

    private static void SetBackgroundPriority(Process process)
    {
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        process.Refresh();
        if (process.PriorityClass is not (ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle))
        {
            throw new InvalidOperationException(
                "The local transcription worker could not enter background priority.");
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string workerExecutablePath,
        string commandPipeHandle,
        string resultPipeHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandPipeHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPipeHandle);
        var startInfo = new ProcessStartInfo
        {
            FileName = workerExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var inheritedEnvironment = startInfo.Environment.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        startInfo.Environment.Clear();
        CopySafeEnvironmentValue(inheritedEnvironment, startInfo, "SystemRoot");
        CopySafeEnvironmentValue(inheritedEnvironment, startInfo, "WINDIR");
        CopySafeEnvironmentValue(inheritedEnvironment, startInfo, "TEMP");
        CopySafeEnvironmentValue(inheritedEnvironment, startInfo, "TMP");
        CopySafeEnvironmentValue(inheritedEnvironment, startInfo, "TMPDIR");
        CopySafeEnvironmentValue(inheritedEnvironment, startInfo, "DOTNET_ROOT");
        CopySafeEnvironmentValue(inheritedEnvironment, startInfo, "DOTNET_ROOT_X64");
        startInfo.ArgumentList.Add(WorkerProcessContract.ModeArgument);
        startInfo.Environment[WorkerProcessContract.CommandPipeEnvironmentVariable] = commandPipeHandle;
        startInfo.Environment[WorkerProcessContract.ResultPipeEnvironmentVariable] = resultPipeHandle;
        return startInfo;
    }

    private static void CopySafeEnvironmentValue(
        IReadOnlyDictionary<string, string?> source,
        ProcessStartInfo destination,
        string name)
    {
        if (source.TryGetValue(name, out var value) && value is not null)
        {
            destination.Environment[name] = value;
        }
    }

    private sealed class SystemWorkerChildProcess(Process process) : IWorkerChildProcess
    {
        public int Id => process.Id;

        public bool HasExited => process.HasExited;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

        public void KillProcessTree()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

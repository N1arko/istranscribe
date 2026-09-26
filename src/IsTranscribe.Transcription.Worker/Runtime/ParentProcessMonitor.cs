using System.Diagnostics;

namespace IsTranscribe.Transcription.Worker.Runtime;

/// <summary>
/// Complements pipe EOF detection so the worker releases native resources when its
/// owning UI process disappears.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public sealed class ParentProcessMonitor : IParentProcessMonitor
{
    public async Task WaitForExitAsync(int parentProcessId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(parentProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return;
            }

            await process.WaitForExitAsync(cancellationToken);
        }
    }
}

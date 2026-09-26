using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Transcription.Worker.Tests.Support;

internal sealed class ConnectedWorkerPipes : IAsyncDisposable
{
    private ConnectedWorkerPipes(
        WorkerFramedConnection parent,
        WorkerFramedConnection child,
        Stream parentCommandOutput,
        Stream parentResultInput,
        Stream childCommandInput,
        Stream childResultOutput)
    {
        Parent = parent;
        Child = child;
        ParentCommandOutput = parentCommandOutput;
        ParentResultInput = parentResultInput;
        ChildCommandInput = childCommandInput;
        ChildResultOutput = childResultOutput;
    }

    public WorkerFramedConnection Parent { get; }

    public WorkerFramedConnection Child { get; }

    public Stream ParentCommandOutput { get; }

    public Stream ParentResultInput { get; }

    public Stream ChildCommandInput { get; }

    public Stream ChildResultOutput { get; }

    public static ConnectedWorkerPipes Create()
    {
        var (parentCommand, childCommand) = TestPipeFactory.CreateOneWay(System.IO.Pipes.PipeDirection.Out);
        var (parentResult, childResult) = TestPipeFactory.CreateOneWay(System.IO.Pipes.PipeDirection.In);

        return new ConnectedWorkerPipes(
            new WorkerFramedConnection(parentResult, parentCommand, ownsStreams: false),
            new WorkerFramedConnection(childCommand, childResult, ownsStreams: false),
            parentCommand,
            parentResult,
            childCommand,
            childResult);
    }

    public async ValueTask DisposeAsync()
    {
        await Parent.DisposeAsync();
        await Child.DisposeAsync();
        await ParentCommandOutput.DisposeAsync();
        await ParentResultInput.DisposeAsync();
        await ChildCommandInput.DisposeAsync();
        await ChildResultOutput.DisposeAsync();
    }
}

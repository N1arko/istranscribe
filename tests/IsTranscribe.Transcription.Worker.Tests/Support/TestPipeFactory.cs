using System.IO.Pipes;

namespace IsTranscribe.Transcription.Worker.Tests.Support;

internal static class TestPipeFactory
{
    public static (Stream Server, Stream Client) CreateOneWay(PipeDirection serverDirection)
    {
        var name = $"itw{Guid.NewGuid():N}"[..11];
        var clientDirection = serverDirection == PipeDirection.Out
            ? PipeDirection.In
            : PipeDirection.Out;
        var server = new NamedPipeServerStream(
            name,
            serverDirection,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            ".",
            name,
            clientDirection,
            PipeOptions.Asynchronous);
        try
        {
            var connected = server.WaitForConnectionAsync();
            client.Connect(timeout: 2_000);
            connected.GetAwaiter().GetResult();
            return (server, client);
        }
        catch
        {
            server.Dispose();
            client.Dispose();
            throw;
        }
    }
}

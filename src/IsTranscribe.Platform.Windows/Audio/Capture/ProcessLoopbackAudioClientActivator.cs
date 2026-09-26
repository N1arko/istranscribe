using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace IsTranscribe.Host.Audio.Capture;

[SupportedOSPlatform("windows")]
internal static class ProcessLoopbackAudioClientActivator
{
    private const short VtBlob = 65;
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int AudioClientActivationTypeProcessLoopback = 1;
    private const int ProcessLoopbackModeIncludeTargetProcessTree = 0;

    public static async ValueTask<AudioClient> ActivateAsync(int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationTypeProcessLoopback,
            ProcessLoopbackParams = new ProcessLoopbackParams
            {
                TargetProcessId = (uint)processId,
                ProcessLoopbackMode = ProcessLoopbackModeIncludeTargetProcessTree
            }
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPointer = Marshal.AllocHGlobal(paramsSize);
        var propVariantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPointer, false);
            Marshal.StructureToPtr(
                new PropVariant
                {
                    vt = VtBlob,
                    blobVal = new Blob
                    {
                        Length = paramsSize,
                        Data = paramsPointer
                    }
                },
                propVariantPointer,
                false);

            var completionHandler = new ActivateAudioInterfaceCompletionHandler();
            var interfaceId = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref interfaceId,
                propVariantPointer,
                completionHandler,
                out var activationOperation));

            using var cancellationRegistration = cancellationToken.Register(completionHandler.TrySetCanceled);
            var audioClient = await completionHandler.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new AudioClient(audioClient);
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPointer);
            Marshal.FreeHGlobal(paramsPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public ProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessLoopbackParams
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<IAudioClient> _taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _taskCompletionSource.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var activateResult, out var activatedInterface);
                Marshal.ThrowExceptionForHR(activateResult);

                if (activatedInterface is not IAudioClient audioClient)
                {
                    throw new InvalidCastException("ActivateAudioInterfaceAsync did not return IAudioClient.");
                }

                _taskCompletionSource.TrySetResult(audioClient);
            }
            catch (Exception exception)
            {
                _taskCompletionSource.TrySetException(exception);
            }
        }

        public void TrySetCanceled() => _taskCompletionSource.TrySetCanceled();
    }
}

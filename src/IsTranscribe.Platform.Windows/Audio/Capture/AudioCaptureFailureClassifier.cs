using System.Runtime.InteropServices;

namespace IsTranscribe.Host.Audio.Capture;

internal static class AudioCaptureFailureClassifier
{
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEEndpointCreateFailed = unchecked((int)0x8889000F);
    private const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);

    public static AudioCaptureFailureKind Classify(Exception exception) =>
        exception switch
        {
            PlatformNotSupportedException => AudioCaptureFailureKind.Unsupported,
            NotSupportedException => AudioCaptureFailureKind.Unsupported,
            UnauthorizedAccessException => AudioCaptureFailureKind.AccessDenied,
            IOException => AudioCaptureFailureKind.IoFailure,
            COMException comException when comException.HResult == EAccessDenied => AudioCaptureFailureKind.AccessDenied,
            COMException comException when comException.HResult is AudclntEDeviceInvalidated or AudclntEEndpointCreateFailed => AudioCaptureFailureKind.DeviceLost,
            COMException comException when comException.HResult == AudclntEUnsupportedFormat => AudioCaptureFailureKind.Unsupported,
            _ => AudioCaptureFailureKind.UnexpectedRuntimeFailure
        };
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using IsTranscribe.Application.Platform;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Windows shell dispatch and active-work-area services.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#desktop
/// </remarks>
public sealed class WindowsPlatformShell : IPlatformShell
{
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public WindowsPlatformShell()
        : this(Process.Start)
    {
    }

    internal WindowsPlatformShell(Func<ProcessStartInfo, Process?> startProcess)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        _startProcess = startProcess;
    }

    public ValueTask OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The recording file is no longer available.", fullPath);
        }

        StartShell(fullPath);
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenContainingFolderAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The recording folder is no longer available.");
        }

        StartShell(directory);
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Only HTTP and HTTPS help links are supported.", nameof(uri));
        }

        StartShell(uri.AbsoluteUri);
        return ValueTask.CompletedTask;
    }

    private void StartShell(string target)
    {
        using var process = _startProcess(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }
}

public sealed class WindowsActiveWorkAreaProvider : IActiveWorkAreaProvider
{
    public PlatformPoint? TryGetForegroundWindowCenter()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var bounds))
        {
            return null;
        }

        return new PlatformPoint(
            bounds.Left + Math.Max(0, bounds.Right - bounds.Left) / 2,
            bounds.Top + Math.Max(0, bounds.Bottom - bounds.Top) / 2);
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

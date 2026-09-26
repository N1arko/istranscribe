using System.Diagnostics;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// </remarks>
public sealed class WindowsPlatformShellTests
{
    [Fact]
    public async Task Successful_shell_dispatch_does_not_require_a_new_process_handle()
    {
        ProcessStartInfo? dispatch = null;
        var shell = new WindowsPlatformShell(startInfo =>
        {
            dispatch = startInfo;
            return null;
        });

        await shell.OpenContainingFolderAsync(
            Path.GetTempPath(),
            CancellationToken.None);

        Assert.NotNull(dispatch);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), dispatch.FileName);
        Assert.True(dispatch.UseShellExecute);
    }

    [Fact]
    public async Task Missing_recording_still_fails_before_shell_dispatch()
    {
        var dispatchCalls = 0;
        var shell = new WindowsPlatformShell(_ =>
        {
            dispatchCalls++;
            return null;
        });
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-missing-{Guid.NewGuid():N}.mp3");

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await shell.OpenFileAsync(missingPath, CancellationToken.None));

        Assert.Equal(0, dispatchCalls);
    }
}

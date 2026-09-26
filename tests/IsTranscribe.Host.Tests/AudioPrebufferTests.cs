using IsTranscribe.Host.Audio.Prebuffer;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class AudioPrebufferTests
{
    [Fact]
    public void SnapshotReturnsBufferedAudioInInsertionOrder()
    {
        var waveFormat = new WaveFormat(4, 8, 1);
        var prebuffer = new AudioPrebuffer(waveFormat, durationSeconds: 2);

        prebuffer.Append([1, 2, 3, 4]);
        prebuffer.Append([5, 6]);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, prebuffer.Snapshot());
    }

    [Fact]
    public void SnapshotKeepsOnlyNewestBytesWhenCapacityIsExceeded()
    {
        var waveFormat = new WaveFormat(4, 8, 1);
        var prebuffer = new AudioPrebuffer(waveFormat, durationSeconds: 1);

        prebuffer.Append([1, 2, 3, 4]);
        prebuffer.Append([5, 6, 7, 8]);

        Assert.Equal(new byte[] { 5, 6, 7, 8 }, prebuffer.Snapshot());
    }

    [Fact]
    public void ClearDropsBufferedAudio()
    {
        var waveFormat = new WaveFormat(8, 16, 1);
        var prebuffer = new AudioPrebuffer(waveFormat, durationSeconds: 1);

        prebuffer.Append([1, 2, 3]);
        prebuffer.Clear();

        Assert.Empty(prebuffer.Snapshot());
    }
}

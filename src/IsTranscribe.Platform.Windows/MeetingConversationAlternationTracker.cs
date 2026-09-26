using IsTranscribe.Core.Detection;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Tracks privacy-reduced render/microphone turn changes over a short local window.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.evidence
/// </remarks>
internal sealed class MeetingConversationAlternationTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MaximumTurnGap = TimeSpan.FromSeconds(5);
    private readonly Queue<DateTimeOffset> _transitions = [];
    private SpeechChannel _lastExclusiveChannel;
    private DateTimeOffset _lastExclusiveAtUtc;

    public double Observe(DateTimeOffset nowUtc, bool renderSpeech, bool microphoneSpeech)
    {
        var current = (renderSpeech, microphoneSpeech) switch
        {
            (true, false) => SpeechChannel.Render,
            (false, true) => SpeechChannel.Microphone,
            _ => SpeechChannel.None
        };

        if (current != SpeechChannel.None && current != _lastExclusiveChannel)
        {
            if (_lastExclusiveChannel != SpeechChannel.None &&
                nowUtc - _lastExclusiveAtUtc <= MaximumTurnGap)
            {
                _transitions.Enqueue(nowUtc);
            }

            _lastExclusiveChannel = current;
            _lastExclusiveAtUtc = nowUtc;
        }
        else if (current != SpeechChannel.None)
        {
            _lastExclusiveAtUtc = nowUtc;
        }

        var cutoff = nowUtc - Window;
        while (_transitions.TryPeek(out var transition) && transition < cutoff)
        {
            _transitions.Dequeue();
        }

        return _transitions.Count < 2
            ? 0
            : Math.Clamp((_transitions.Count - 1) / 3d, 0, 1);
    }

    public void Reset()
    {
        _transitions.Clear();
        _lastExclusiveChannel = SpeechChannel.None;
        _lastExclusiveAtUtc = default;
    }

    private enum SpeechChannel
    {
        None,
        Render,
        Microphone
    }
}

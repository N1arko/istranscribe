using System.Text;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Host.Transcription;

public static class TranscriptionMarkdownRenderer
{
    // @spec spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#artifact-materialization.markdown-structure
    public static string Render(MeetingSessionTranscriptionWorkItem session, TranscriptionResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {session.SourceApp} transcription");
        builder.AppendLine();
        builder.AppendLine("## Metadata");
        builder.AppendLine();
        builder.AppendLine($"- SessionId: `{session.Id}`");
        builder.AppendLine($"- CreatedAt (UTC): {session.CreatedAtUtc:O}");
        if (session.StartedAtUtc.HasValue)
            builder.AppendLine($"- StartedAt (UTC): {session.StartedAtUtc.Value:O}");
        if (session.EndedAtUtc.HasValue)
            builder.AppendLine($"- EndedAt (UTC): {session.EndedAtUtc.Value:O}");
        if (session.DurationSeconds.HasValue)
            builder.AppendLine($"- Duration: {FormatDuration(session.DurationSeconds.Value)}");
        builder.AppendLine($"- SourceApp: {session.SourceApp}");
        builder.AppendLine($"- Mode: {session.Mode}");
        if (!string.IsNullOrWhiteSpace(session.OutputDeviceId))
            builder.AppendLine($"- OutputDevice: {session.OutputDeviceId}");
        if (!string.IsNullOrWhiteSpace(session.MicrophoneDeviceId))
            builder.AppendLine($"- MicrophoneDevice: {session.MicrophoneDeviceId}");
        builder.AppendLine($"- Model: {session.TranscriptionModel ?? "unknown"}");
        builder.AppendLine($"- Diarization: {(session.DiarizationEnabled ? "enabled" : "disabled")}");
        builder.AppendLine();
        builder.AppendLine("## Transcript");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(response.Text)
            ? "(provider returned empty transcript text)"
            : response.Text.Trim());
        return builder.ToString();
    }

    private static string FormatDuration(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}

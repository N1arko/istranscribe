using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.IO;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.App.Windows;

public sealed class OptionItem(string value, string label)
{
    public string Value { get; } = value;

    public string Label { get; } = label;

    public override string ToString() => Label;
}

public sealed class SelectableProcessItem(string displayName, string processName, bool hasAudioActivity, bool wasRecentlyActive)
    : INotifyPropertyChanged
{
    private bool _isSelected;

    public string DisplayName { get; } = displayName;

    public string ProcessName { get; } = processName;

    public bool HasAudioActivity { get; } = hasAudioActivity;

    public bool WasRecentlyActive { get; } = wasRecentlyActive;

    public string ActivityLabel => HasAudioActivity ? "Audio active now" : "Recent audio activity";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SelectableAppSuggestionItem(string displayName, string processName, string statusLabel)
    : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _statusLabel = statusLabel;

    public string DisplayName { get; } = displayName;

    public string ProcessName { get; } = processName;

    public string StatusLabel
    {
        get => _statusLabel;
        set
        {
            if (_statusLabel == value)
            {
                return;
            }

            _statusLabel = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class EditableAppRuleItem(string id, string displayName, string processName, bool enabled, bool isBuiltIn = false)
    : INotifyPropertyChanged
{
    private string _displayName = displayName;
    private string _processName = processName;
    private bool _enabled = enabled;

    public string Id { get; } = id;

    public bool IsBuiltIn { get; } = isBuiltIn;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            OnPropertyChanged();
        }
    }

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (_processName == value)
            {
                return;
            }

            _processName = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            OnPropertyChanged();
        }
    }

    public AppRuleRecord ToRecord() =>
        AppRuleRecord.Normalize(new AppRuleRecord(Id, DisplayName, ProcessName, Enabled));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class EditableStringItem(string value, bool isBuiltIn = false) : INotifyPropertyChanged
{
    private string _value = value;

    public bool IsBuiltIn { get; } = isBuiltIn;

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class EditableCollection<T> : ObservableCollection<T>
{
    public EditableCollection()
    {
    }

    public EditableCollection(IEnumerable<T> items)
        : base(items)
    {
    }
}

public sealed class HomeMeetingSessionItem(MeetingSessionListItem session)
{
    public string Id { get; } = session.Id;

    public string SourceApp { get; } = string.IsNullOrWhiteSpace(session.SourceApp) ? "Unknown app" : session.SourceApp;

    public DateTimeOffset? StartedAtUtc { get; } = session.StartedAtUtc;

    public DateTimeOffset? EndedAtUtc { get; } = session.EndedAtUtc;

    public string RecordingStatus { get; } = session.RecordingStatus;

    public string TranscriptionStatus { get; } = session.TranscriptionStatus;

    public string? TranscriptMarkdownPath { get; } = session.TranscriptMarkdownPath;

    public string? SessionDirectoryPath { get; } = session.SessionDirectoryPath;

    public string? AudioOutputPath { get; } = session.AudioOutputPath;

    public string? AudioMicPath { get; } = session.AudioMicPath;

    public string? AudioMixPath { get; } = session.AudioMixPath;

    public string StartedAtLabel => StartedAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Unknown start";

    public string EndedAtLabel => EndedAtUtc is null ? "In progress" : EndedAtUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string DurationLabel => BuildDurationLabel(session.DurationSeconds, StartedAtUtc, EndedAtUtc);

    public string ErrorLabel => string.IsNullOrWhiteSpace(session.ErrorCode)
        ? string.Empty
        : string.IsNullOrWhiteSpace(session.ErrorMessage)
            ? session.ErrorCode
            : $"{session.ErrorCode}: {session.ErrorMessage}";

    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(SessionDirectoryPath) && Directory.Exists(SessionDirectoryPath);

    public bool CanOpenMarkdown => !string.IsNullOrWhiteSpace(TranscriptMarkdownPath) && File.Exists(TranscriptMarkdownPath);

    public bool CanRetryTranscription =>
        string.Equals(TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase)
        && HasAnyAudioArtifact();

    private bool HasAnyAudioArtifact() =>
        IsCandidatePath(AudioMixPath)
        || IsCandidatePath(AudioOutputPath)
        || IsCandidatePath(AudioMicPath);

    private static bool IsCandidatePath(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static string BuildDurationLabel(double? durationSeconds, DateTimeOffset? startedAtUtc, DateTimeOffset? endedAtUtc)
    {
        if (durationSeconds.HasValue && durationSeconds.Value >= 0)
        {
            return FormatDuration(TimeSpan.FromSeconds(durationSeconds.Value));
        }

        if (startedAtUtc.HasValue && endedAtUtc.HasValue && endedAtUtc >= startedAtUtc)
        {
            return FormatDuration(endedAtUtc.Value - startedAtUtc.Value);
        }

        return "--";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}

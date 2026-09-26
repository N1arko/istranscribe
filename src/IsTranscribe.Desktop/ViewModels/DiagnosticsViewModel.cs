using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Services;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// Privacy-bounded diagnostic summary with explicit copy/export/log actions.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#surfaces
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public sealed class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private const int MaximumEngineIdLength = 128;
    private const int MaximumModelIdLength = 256;
    private const int MaximumProviderRequestIdLength = 128;
    private const int MaximumBackendLength = 64;
    private const int MaximumRuntimeVersionLength = 128;
    private readonly IApplicationRuntime _runtime;
    private readonly ILocalizationService _strings;
    private readonly IPlatformShell _shell;
    private readonly string _logsDirectory;
    private readonly string _defaultRecordingsDirectory;
    private string? _statusMessage;
    private string? _errorMessage;
    private string? _statusMessageKey;
    private string? _errorMessageKey;
    private bool _disposed;

    public DiagnosticsViewModel(
        IApplicationRuntime runtime,
        ILocalizationService strings,
        IPlatformShell shell)
    {
        _runtime = runtime;
        _strings = strings;
        _shell = shell;
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "isTranscribe");
        _logsDirectory = Path.Combine(localRoot, "logs");
        _defaultRecordingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "isTranscribe",
            "Recordings");

        CopyCommand = new RelayCommand(() => CopyRequested?.Invoke(this, EventArgs.Empty));
        ExportCommand = new RelayCommand(() => ExportRequested?.Invoke(this, EventArgs.Empty));
        OpenLogsCommand = new AsyncRelayCommand(OpenLogsAsync);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        _runtime.SnapshotChanged += Runtime_OnSnapshotChanged;
        _strings.LanguageChanged += Strings_OnLanguageChanged;
        Rebuild(_runtime.Snapshot);
    }

    public event EventHandler? CopyRequested;

    public event EventHandler? ExportRequested;

    public event EventHandler? CloseRequested;

    public ObservableCollection<DiagnosticRowViewModel> Rows { get; } = [];

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand ExportCommand { get; }

    public IAsyncRelayCommand OpenLogsCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public string SummaryText => string.Join(
        Environment.NewLine,
        Rows.Select(static row => $"{row.Label}: {row.Value}"));

    public string TextFileTypeLabel => _strings.Get("String.FileType.Text");

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public void MarkCopied() => SetStatus("String.Diagnostics.Copied");

    public void MarkExported() => SetStatus("String.Diagnostics.Exported");

    public void MarkActionFailed()
    {
        SetStatusMessage(null);
        SetErrorMessage("String.Diagnostics.Unavailable");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.SnapshotChanged -= Runtime_OnSnapshotChanged;
        _strings.LanguageChanged -= Strings_OnLanguageChanged;
    }

    private async Task OpenLogsAsync()
    {
        try
        {
            await _shell.OpenContainingFolderAsync(_logsDirectory, CancellationToken.None);
        }
        catch
        {
            MarkActionFailed();
        }
    }

    private void SetStatus(string key)
    {
        SetErrorMessage(null);
        SetStatusMessage(key);
    }

    private void Runtime_OnSnapshotChanged(object? sender, ApplicationRuntimeSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Rebuild(snapshot);
            return;
        }

        Dispatcher.UIThread.Post(() => Rebuild(snapshot));
    }

    private void Strings_OnLanguageChanged(object? sender, EventArgs args)
    {
        Rebuild(_runtime.Snapshot);
        RefreshLocalizedMessages();
    }

    private void Rebuild(ApplicationRuntimeSnapshot snapshot)
    {
        Rows.Clear();
        Rows.Add(Row("Version", GetVersion()));
        Rows.Add(Row("OperatingSystem", RuntimeInformation.OSDescription.Trim()));
        Rows.Add(Row("Architecture", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()));
        Rows.Add(Row("Activity", _strings.Get($"String.State.{snapshot.Activity}.Title")));
        Rows.Add(Row("Service", _strings.Get(
            snapshot.ServiceEnabled ? "String.Service.Listening" : "String.Service.Paused")));
        AppendTranscriptionRows(snapshot);
        Rows.Add(Row("Capability", _strings.Get(snapshot.Capability.State switch
        {
            RuntimeCapabilityState.Full => "String.Diagnostics.Capability.Full",
            RuntimeCapabilityState.Degraded => "String.Diagnostics.Capability.Degraded",
            RuntimeCapabilityState.Blocked => "String.Diagnostics.Capability.Blocked",
            _ => "String.Diagnostics.Capability.Unknown"
        })));
        Rows.Add(Row("CapabilityIssue", LocalizeCapabilityIssue(snapshot.Capability.Issue)));
        Rows.Add(Row(
            "OutputReadiness",
            _strings.Get(snapshot.Capability.HasActiveOutput
                ? "String.Diagnostics.Endpoint.Ready"
                : "String.Diagnostics.Endpoint.Missing")));
        Rows.Add(Row(
            "MicrophoneReadiness",
            _strings.Get(snapshot.Capability.HasActiveMicrophone
                ? "String.Diagnostics.Endpoint.Ready"
                : "String.Diagnostics.Endpoint.Missing")));
        Rows.Add(Row(
            "Language",
            _strings.Get(_strings.CurrentLanguage == UiLanguage.English
                ? "String.Settings.Language.English"
                : "String.Settings.Language.Russian")));
        Rows.Add(Row("Theme", _strings.Get(snapshot.UserSettings.Theme switch
        {
            "light" => "String.Settings.Theme.Light",
            "dark" => "String.Settings.Theme.Dark",
            _ => "String.Settings.Theme.System"
        })));
        Rows.Add(Row("Microphones", snapshot.AvailableMicrophones.Count.ToString(_strings.CurrentCulture)));
        Rows.Add(Row("Recordings", snapshot.RecentRecordings.Count.ToString(_strings.CurrentCulture)));
        Rows.Add(Row(
            "RecordingsFolder",
            snapshot.UserSettings.RecordingsFolder ?? _defaultRecordingsDirectory));
        Rows.Add(Row("LogsFolder", _logsDirectory));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TextFileTypeLabel));
    }

    private void AppendTranscriptionRows(ApplicationRuntimeSnapshot snapshot)
    {
        var jobs = snapshot.RecentRecordings
            .Select(static recording => recording.Transcription)
            .Where(static job => job is not null)
            .Cast<RuntimeTranscriptionJobSnapshot>()
            .ToArray();
        var job = jobs.FirstOrDefault(static candidate => candidate.State is
                RuntimeTranscriptionJobState.Queued
                or RuntimeTranscriptionJobState.Preparing
                or RuntimeTranscriptionJobState.Uploading
                or RuntimeTranscriptionJobState.Processing)
            ?? jobs.FirstOrDefault(static candidate => candidate.State is
                RuntimeTranscriptionJobState.RetryScheduled
                or RuntimeTranscriptionJobState.AttentionRequired)
            ?? jobs.FirstOrDefault();
        if (job is null)
        {
            return;
        }

        Rows.Add(Row(
            "TranscriptionEngine",
            NormalizeDiagnosticIdentifier(job.EngineId, MaximumEngineIdLength) ?? "—"));
        Rows.Add(Row(
            "TranscriptionModel",
            NormalizeDiagnosticIdentifier(job.ModelId, MaximumModelIdLength) ?? "—"));

        var chunkCount = Math.Max(0, job.ChunkCount);
        var chunkValue = job.CurrentChunkIndex is { } currentChunkIndex
                         && currentChunkIndex >= 0
                         && currentChunkIndex < chunkCount
            ? _strings.Format(
                "String.Diagnostics.Transcription.Chunks.Current.Format",
                currentChunkIndex + 1,
                chunkCount)
            : chunkCount.ToString(_strings.CurrentCulture);
        Rows.Add(Row("TranscriptionChunks", chunkValue));

        var providerRequestId = SanitizeProviderRequestId(job.ProviderRequestId);
        if (providerRequestId is not null)
        {
            Rows.Add(Row("TranscriptionProviderRequestId", providerRequestId));
        }

        AppendLocalTranscriptionRows(job.LocalDiagnostics);
    }

    private void AppendLocalTranscriptionRows(RuntimeLocalTranscriptionDiagnosticsSnapshot? diagnostics)
    {
        if (diagnostics is null)
        {
            return;
        }

        Rows.Add(Row(
            "TranscriptionRequestedBackend",
            NormalizeDiagnosticIdentifier(diagnostics.RequestedBackend, MaximumBackendLength) ?? "—"));
        Rows.Add(Row(
            "TranscriptionResolvedBackend",
            NormalizeDiagnosticIdentifier(diagnostics.ResolvedBackend, MaximumBackendLength) ?? "—"));
        Rows.Add(Row(
            "TranscriptionThreads",
            Math.Max(0, diagnostics.ThreadCount).ToString(_strings.CurrentCulture)));
        Rows.Add(Row(
            "TranscriptionRuntimeVersion",
            NormalizeDiagnosticIdentifier(diagnostics.RuntimeVersion, MaximumRuntimeVersionLength) ?? "—"));
        Rows.Add(Row(
            "TranscriptionNativeManifest",
            NormalizeSha256(diagnostics.NativeBundleManifestSha256) ?? "—"));
        Rows.Add(Row(
            "TranscriptionModelSha256",
            NormalizeSha256(diagnostics.ModelSha256) ?? "—"));
        if (diagnostics.ProcessingDurationMilliseconds is >= 0 and var durationMilliseconds)
        {
            Rows.Add(Row(
                "TranscriptionProcessingDuration",
                FormatDuration(TimeSpan.FromMilliseconds(durationMilliseconds))));
        }
    }

    private static string? NormalizeDiagnosticIdentifier(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumLength
               && normalized.All(static character => char.IsLetterOrDigit(character)
                   || character is '-' or '_' or '.' or ':' or '/')
            ? normalized
            : null;
    }

    private static string? SanitizeProviderRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var normalized = requestId.Trim();
        return normalized.Length <= MaximumProviderRequestIdLength
               && !LooksSensitiveProviderRequestId(normalized)
               && normalized.All(static character => character is >= 'a' and <= 'z'
                   or >= 'A' and <= 'Z'
                   or >= '0' and <= '9'
                   or '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToLowerInvariant()
            : null;
    }

    private string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (long)Math.Round(duration.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? _strings.Format(
                "String.Diagnostics.Transcription.Duration.Hours.Format",
                hours,
                minutes,
                seconds)
            : _strings.Format(
                "String.Diagnostics.Transcription.Duration.Minutes.Format",
                minutes,
                seconds);
    }

    private static bool LooksSensitiveProviderRequestId(string value) =>
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || value.Contains("gsk_", StringComparison.OrdinalIgnoreCase)
        || value.Contains("sk-or-v1-", StringComparison.OrdinalIgnoreCase);

    private void SetStatusMessage(string? resourceKey)
    {
        _statusMessageKey = resourceKey;
        StatusMessage = resourceKey is null ? null : _strings.Get(resourceKey);
    }

    private void SetErrorMessage(string? resourceKey)
    {
        _errorMessageKey = resourceKey;
        ErrorMessage = resourceKey is null ? null : _strings.Get(resourceKey);
    }

    private void RefreshLocalizedMessages()
    {
        StatusMessage = _statusMessageKey is null ? null : _strings.Get(_statusMessageKey);
        ErrorMessage = _errorMessageKey is null ? null : _strings.Get(_errorMessageKey);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    private string LocalizeCapabilityIssue(RuntimeCapabilityIssue issue) => _strings.Get(issue switch
    {
        RuntimeCapabilityIssue.PlatformBlocked =>
            "String.Diagnostics.CapabilityIssue.PlatformBlocked",
        RuntimeCapabilityIssue.NoActiveOutput =>
            "String.Diagnostics.CapabilityIssue.NoActiveOutput",
        RuntimeCapabilityIssue.NoActiveMicrophone =>
            "String.Diagnostics.CapabilityIssue.NoActiveMicrophone",
        RuntimeCapabilityIssue.NoActiveAudioEndpoints =>
            "String.Diagnostics.CapabilityIssue.NoActiveAudioEndpoints",
        RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable =>
            "String.Diagnostics.CapabilityIssue.ConfiguredMicrophoneUnavailable",
        RuntimeCapabilityIssue.MicrophoneCaptureUnavailable =>
            "String.Diagnostics.CapabilityIssue.MicrophoneCaptureUnavailable",
        RuntimeCapabilityIssue.OutputCaptureUnavailable =>
            "String.Diagnostics.CapabilityIssue.OutputCaptureUnavailable",
        RuntimeCapabilityIssue.ProcessOutputCaptureUnavailable =>
            "String.Diagnostics.CapabilityIssue.ProcessOutputCaptureUnavailable",
        _ => "String.Diagnostics.CapabilityIssue.None"
    });

    private DiagnosticRowViewModel Row(string keySuffix, string value) => new(
        _strings.Get($"String.Diagnostics.Field.{keySuffix}"),
        value);

    private static string GetVersion()
    {
        var version = typeof(DiagnosticsViewModel).Assembly.GetName().Version;
        return version is null ? "—" : version.ToString(3);
    }
}

public sealed record DiagnosticRowViewModel(string Label, string Value);

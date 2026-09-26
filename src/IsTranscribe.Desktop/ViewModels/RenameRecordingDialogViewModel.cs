using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Core.Runtime;
using IsTranscribe.Desktop.Localization;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// Presentation state for assigning a stable display title to a recording.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class RenameRecordingDialogViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _strings;
    private readonly string _initialName;
    private string _name;
    private bool _disposed;

    public RenameRecordingDialogViewModel(ILocalizationService strings, string currentName)
    {
        _strings = strings;
        _initialName = RecentRecordingTitle.Normalize(currentName);
        _name = _initialName;
        SaveCommand = new RelayCommand(Save, CanSave);
        CancelCommand = new RelayCommand(Cancel);
        _strings.LanguageChanged += Strings_OnLanguageChanged;
    }

    public event EventHandler<RenameRecordingDialogResolvedEventArgs>? Resolved;

    public string DialogTitle => _strings.Get("String.RenameRecording.Title");

    public string FieldLabel => _strings.Get("String.RenameRecording.Field");

    public string SaveAction => _strings.Get("String.Action.Save");

    public string CancelAction => _strings.Get("String.Action.Cancel");

    public int MaximumNameLength => RecentRecordingTitle.MaxLength;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _strings.LanguageChanged -= Strings_OnLanguageChanged;
    }

    private bool CanSave()
    {
        try
        {
            return !string.Equals(
                RecentRecordingTitle.Normalize(Name),
                _initialName,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void Save() =>
        Resolved?.Invoke(
            this,
            new RenameRecordingDialogResolvedEventArgs(
                isConfirmed: true,
                RecentRecordingTitle.Normalize(Name)));

    private void Cancel() =>
        Resolved?.Invoke(this, new RenameRecordingDialogResolvedEventArgs(isConfirmed: false, null));

    private void Strings_OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(FieldLabel));
        OnPropertyChanged(nameof(SaveAction));
        OnPropertyChanged(nameof(CancelAction));
    }
}

public sealed class RenameRecordingDialogResolvedEventArgs(bool isConfirmed, string? name) : EventArgs
{
    public bool IsConfirmed { get; } = isConfirmed;

    public string? Name { get; } = name;
}

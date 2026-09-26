using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Desktop.Localization;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// Reusable explicit confirmation model for destructive recording actions.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class ConfirmationDialogViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _strings;
    private readonly string _titleKey;
    private readonly string _descriptionKey;
    private readonly string _primaryActionKey;
    private readonly string _cancelActionKey;
    private readonly string? _destructiveActionKey;
    private bool _disposed;

    public ConfirmationDialogViewModel(
        ILocalizationService strings,
        string titleKey,
        string descriptionKey,
        string primaryActionKey,
        string cancelActionKey,
        string? destructiveActionKey = null,
        bool primaryIsDestructive = false)
    {
        _strings = strings;
        _titleKey = titleKey;
        _descriptionKey = descriptionKey;
        _primaryActionKey = primaryActionKey;
        _cancelActionKey = cancelActionKey;
        _destructiveActionKey = destructiveActionKey;
        PrimaryIsDestructive = primaryIsDestructive;
        PrimaryCommand = new RelayCommand(() => Resolved?.Invoke(this, ConfirmationDialogResult.Primary));
        DestructiveCommand = new RelayCommand(() => Resolved?.Invoke(this, ConfirmationDialogResult.Destructive));
        CancelCommand = new RelayCommand(() => Resolved?.Invoke(this, ConfirmationDialogResult.Cancel));
        _strings.LanguageChanged += Strings_OnLanguageChanged;
    }

    public event EventHandler<ConfirmationDialogResult>? Resolved;

    public string Title => _strings.Get(_titleKey);

    public string Description => _strings.Get(_descriptionKey);

    public string PrimaryAction => _strings.Get(_primaryActionKey);

    public string CancelAction => _strings.Get(_cancelActionKey);

    public string? DestructiveAction => _destructiveActionKey is null
        ? null
        : _strings.Get(_destructiveActionKey);

    public bool HasDestructiveAction => _destructiveActionKey is not null;

    public bool PrimaryIsDestructive { get; }

    public bool PrimaryIsSafe => !PrimaryIsDestructive;

    public IRelayCommand PrimaryCommand { get; }

    public IRelayCommand DestructiveCommand { get; }

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

    private void Strings_OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(PrimaryAction));
        OnPropertyChanged(nameof(CancelAction));
        OnPropertyChanged(nameof(DestructiveAction));
    }
}

public enum ConfirmationDialogResult
{
    Cancel,
    Primary,
    Destructive
}

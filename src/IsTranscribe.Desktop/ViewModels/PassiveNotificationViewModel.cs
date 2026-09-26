using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Desktop.Localization;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// Localized passive notification for a ready recording or actionable failure.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#surfaces
/// </remarks>
public sealed class PassiveNotificationViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _strings;
    private readonly string _titleKey;
    private readonly string _bodyKey;
    private readonly string? _bodyArgument;
    private readonly bool _localizeMeetingSourceArgument;
    private bool _disposed;

    public PassiveNotificationViewModel(
        ILocalizationService strings,
        string titleKey,
        string bodyKey,
        string? bodyArgument = null,
        bool localizeMeetingSourceArgument = false)
    {
        _strings = strings;
        _titleKey = titleKey;
        _bodyKey = bodyKey;
        _bodyArgument = bodyArgument;
        _localizeMeetingSourceArgument = localizeMeetingSourceArgument;
        OpenCommand = new RelayCommand(() => OpenRequested?.Invoke(this, EventArgs.Empty));
        DismissCommand = new RelayCommand(() => DismissRequested?.Invoke(this, EventArgs.Empty));
        _strings.LanguageChanged += Strings_OnLanguageChanged;
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? DismissRequested;

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand DismissCommand { get; }

    public string Title => _strings.Get(_titleKey);

    public string Body => _bodyArgument is null
        ? _strings.Get(_bodyKey)
        : _strings.Format(
            _bodyKey,
            _localizeMeetingSourceArgument
                ? MeetingSourceLabelLocalizer.Localize(_strings, _bodyArgument)
                : _bodyArgument);

    public string OpenActionText => _strings.Get("String.Action.OpenApp");

    public string DismissActionText => _strings.Get("String.Action.Close");

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
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(OpenActionText));
        OnPropertyChanged(nameof(DismissActionText));
    }
}

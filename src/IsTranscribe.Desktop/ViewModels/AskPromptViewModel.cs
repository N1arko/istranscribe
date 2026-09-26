using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// Presentation and decision lifecycle for one detected-meeting prompt.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class AskPromptViewModel : ObservableObject, IDisposable
{
    private const string AskTitleKey = "String.Ask.Title";
    private const string AskAppLineFormatKey = "String.Ask.AppLine.Format";
    private const string AskCountdownFormatKey = "String.Ask.Countdown.Format";
    private const string AskCountdownAccessibleOneKey = "String.Ask.Countdown.Accessible.One";
    private const string AskCountdownAccessibleFewKey = "String.Ask.Countdown.Accessible.Few";
    private const string AskCountdownAccessibleManyKey = "String.Ask.Countdown.Accessible.Many";
    private const string AskIgnoreApplicationKey = "String.Ask.IgnoreApplication";
    private const string AskWhyShownKey = "String.Ask.WhyShown";
    private const string AskAutomationNameKey = "String.Ask.AutomationName";
    private const string AskReasonAudioAndApplicationKey = "String.Ask.Reason.AudioAndApplication";
    private const string AskReasonAudioConversationKey = "String.Ask.Reason.AudioConversation";
    private const string AskErrorKey = "String.Ask.Error";
    private const string RecordActionKey = "String.Action.Record";
    private const string SkipActionKey = "String.Action.Skip";
    private const string MoreActionKey = "String.Action.More";

    private readonly IApplicationRuntime _runtime;
    private readonly ILocalizationService _localization;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private MeetingPromptSnapshot _prompt;
    private TimeSpan _remaining;
    private bool _hasResolutionError;
    private bool _isResolving;
    private bool _isResolved;
    private bool _disposed;
    private int _resolutionState;

    public AskPromptViewModel(
        IApplicationRuntime runtime,
        ILocalizationService localization,
        MeetingPromptSnapshot prompt,
        TimeProvider timeProvider,
        bool showAdvancedDiagnostics = false)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ShowAdvancedDiagnostics = showAdvancedDiagnostics;

        RecordCommand = new AsyncRelayCommand(RecordAsync, CanResolve);
        SkipCommand = new AsyncRelayCommand(SkipAsync, CanResolve);
        IgnoreApplicationCommand = new AsyncRelayCommand(IgnoreApplicationAsync, CanResolve);
        _localization.LanguageChanged += Localization_OnLanguageChanged;
        UpdateCountdownPresentation();
    }

    public event EventHandler<AskPromptResolvedEventArgs>? Resolved;

    public IAsyncRelayCommand RecordCommand { get; }

    public IAsyncRelayCommand SkipCommand { get; }

    public IAsyncRelayCommand IgnoreApplicationCommand { get; }

    public string CandidateId => _prompt.CandidateId;

    public string SourceLabel => MeetingSourceLabelLocalizer.Localize(
        _localization,
        _prompt.SourceLabel,
        _prompt.ProfileId);

    public DateTimeOffset ExpiresAtUtc => _prompt.ExpiresAtUtc;

    public TimeSpan Remaining
    {
        get => _remaining;
        private set => SetProperty(ref _remaining, value);
    }

    public bool ShowAdvancedDiagnostics { get; }

    public bool IsResolving
    {
        get => _isResolving;
        private set
        {
            if (SetProperty(ref _isResolving, value))
            {
                OnPropertyChanged(nameof(IsInteractionEnabled));
            }
        }
    }

    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (SetProperty(ref _isResolved, value))
            {
                OnPropertyChanged(nameof(IsInteractionEnabled));
            }
        }
    }

    public bool IsInteractionEnabled => !IsResolving && !IsResolved;

    public bool HasResolutionError
    {
        get => _hasResolutionError;
        private set => SetProperty(ref _hasResolutionError, value);
    }

    public string Title => _localization.Get(AskTitleKey);

    public string AppLine => _localization.Format(AskAppLineFormatKey, SourceLabel);

    public string CountdownText => _localization.Format(
        AskCountdownFormatKey,
        FormatRemaining(Remaining));

    public string CountdownAutomationText => _localization.Format(
        ResolveAccessibleCountdownKey(GetRemainingWholeSeconds(Remaining)),
        GetRemainingWholeSeconds(Remaining));

    public string RecordActionText => _localization.Get(RecordActionKey);

    public string SkipActionText => _localization.Get(SkipActionKey);

    public string MoreActionText => _localization.Get(MoreActionKey);

    public string IgnoreApplicationText => _localization.Get(AskIgnoreApplicationKey);

    public string WhyShownText => _localization.Get(AskWhyShownKey);

    public string AutomationName => _localization.Get(AskAutomationNameKey);

    public string DecisionReasonText => _localization.Get(ResolveDecisionReasonKey());

    public string ResolutionErrorText => _localization.Get(AskErrorKey);

    public Task RecordAsync() => ResolveAsync(MeetingPromptUserAction.Record, isTimeout: false);

    public Task SkipAsync() => ResolveAsync(MeetingPromptUserAction.Skip, isTimeout: false);

    public Task IgnoreApplicationAsync() =>
        ResolveAsync(MeetingPromptUserAction.IgnoreApplication, isTimeout: false);

    /// <summary>
    /// Runs a low-frequency countdown and resolves an expired prompt as Skip.
    /// </summary>
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
    public async Task RunCountdownAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            cancellationToken);
        var linkedToken = linkedCancellation.Token;

        try
        {
            while (!IsResolved)
            {
                linkedToken.ThrowIfCancellationRequested();
                var remaining = UpdateCountdownPresentation();
                if (remaining <= TimeSpan.Zero)
                {
                    await ResolveAsync(
                            MeetingPromptUserAction.Skip,
                            isTimeout: true,
                            linkedToken)
                        .ConfigureAwait(true);
                    return;
                }

                var delay = remaining < TimeSpan.FromSeconds(1)
                    ? remaining
                    : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, _timeProvider, linkedToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
        {
            // Prompt replacement, runtime resolution and application shutdown all cancel the ticker.
        }
    }

    /// <summary>
    /// Refreshes mutable prompt metadata while preserving one visible prompt for the candidate.
    /// </summary>
    public void UpdatePrompt(MeetingPromptSnapshot prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!string.Equals(prompt.CandidateId, CandidateId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Prompt candidate identity cannot change in place.", nameof(prompt));
        }

        _prompt = prompt;
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(ExpiresAtUtc));
        OnPropertyChanged(nameof(AppLine));
        OnPropertyChanged(nameof(DecisionReasonText));
        UpdateCountdownPresentation();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private bool CanResolve() => IsInteractionEnabled && !_disposed;

    private async Task ResolveAsync(
        MeetingPromptUserAction action,
        bool isTimeout,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || Interlocked.CompareExchange(ref _resolutionState, 1, 0) != 0)
        {
            return;
        }

        IsResolving = true;
        HasResolutionError = false;
        NotifyCommandsCanExecuteChanged();
        var resolved = false;

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token,
                cancellationToken);
            await _runtime.ResolveMeetingPromptAsync(
                    CandidateId,
                    action,
                    linkedCancellation.Token)
                .ConfigureAwait(true);
            resolved = true;
            Interlocked.Exchange(ref _resolutionState, 2);
            IsResolved = true;
            _lifetimeCancellation.Cancel();
        }
        catch (OperationCanceledException) when (
            _lifetimeCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            // A replaced/disposed prompt must not surface an action error.
        }
        catch (Exception)
        {
            HasResolutionError = true;
        }
        finally
        {
            if (!resolved)
            {
                Interlocked.Exchange(ref _resolutionState, 0);
            }

            IsResolving = false;
            NotifyCommandsCanExecuteChanged();
        }

        if (resolved)
        {
            Resolved?.Invoke(this, new AskPromptResolvedEventArgs(action, isTimeout));
        }
    }

    private TimeSpan UpdateCountdownPresentation()
    {
        var remaining = _prompt.ExpiresAtUtc - _timeProvider.GetUtcNow();
        Remaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(CountdownAutomationText));
        return remaining;
    }

    private string FormatRemaining(TimeSpan remaining)
    {
        var totalSeconds = GetRemainingWholeSeconds(remaining);
        return TimeSpan.FromSeconds(totalSeconds).ToString(@"m\:ss", _localization.CurrentCulture);
    }

    private static int GetRemainingWholeSeconds(TimeSpan remaining) =>
        Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));

    private string ResolveAccessibleCountdownKey(int seconds)
    {
        if (!string.Equals(
                _localization.CurrentCulture.TwoLetterISOLanguageName,
                "ru",
                StringComparison.OrdinalIgnoreCase))
        {
            return seconds == 1 ? AskCountdownAccessibleOneKey : AskCountdownAccessibleManyKey;
        }

        var moduloHundred = seconds % 100;
        if (moduloHundred is >= 11 and <= 14)
        {
            return AskCountdownAccessibleManyKey;
        }

        return (seconds % 10) switch
        {
            1 => AskCountdownAccessibleOneKey,
            >= 2 and <= 4 => AskCountdownAccessibleFewKey,
            _ => AskCountdownAccessibleManyKey
        };
    }

    private string ResolveDecisionReasonKey() => _prompt.DecisionReasons.Any(static reason =>
        reason.Contains("conversation", StringComparison.OrdinalIgnoreCase))
        ? AskReasonAudioConversationKey
        : AskReasonAudioAndApplicationKey;

    private void Localization_OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AppLine));
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(CountdownAutomationText));
        OnPropertyChanged(nameof(RecordActionText));
        OnPropertyChanged(nameof(SkipActionText));
        OnPropertyChanged(nameof(MoreActionText));
        OnPropertyChanged(nameof(IgnoreApplicationText));
        OnPropertyChanged(nameof(WhyShownText));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(DecisionReasonText));
        OnPropertyChanged(nameof(ResolutionErrorText));
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        RecordCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        IgnoreApplicationCommand.NotifyCanExecuteChanged();
    }
}

public sealed class AskPromptResolvedEventArgs(
    MeetingPromptUserAction action,
    bool isTimeout) : EventArgs
{
    public MeetingPromptUserAction Action { get; } = action;

    public bool IsTimeout { get; } = isTimeout;
}

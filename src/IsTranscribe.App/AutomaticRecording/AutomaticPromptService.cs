using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using IsTranscribe.App.Strings;

namespace IsTranscribe.App.AutomaticRecording;

public enum AutomaticPromptDecision
{
    StartRecording,
    No,
    AddAndStart,
    StartOnce,
    NotNow,
    IgnoreThisApp,
    SwitchNow,
    FinishAndStartNew,
    KeepCurrentIfPossible,
    Cancelled
}

public sealed record AutomaticPromptRequest(
    string PromptKind,
    string SourceApp,
    string ProcessName,
    int TimeoutSeconds);

public interface IAutomaticPromptService
{
    Task<AutomaticPromptDecision> ShowAsync(AutomaticPromptRequest request, CancellationToken cancellationToken);
}

public sealed class AutomaticPromptService(Dispatcher dispatcher) : IAutomaticPromptService
{
    private readonly Dispatcher _dispatcher = dispatcher;

    public Task<AutomaticPromptDecision> ShowAsync(AutomaticPromptRequest request, CancellationToken cancellationToken)
    {
        var taskCompletionSource = new TaskCompletionSource<AutomaticPromptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.InvokeAsync(() =>
        {
            var defaultDecision = IsUnknownPrompt(request)
                ? AutomaticPromptDecision.NotNow
                : IsDeviceContinuityPinnedPrompt(request)
                    ? AutomaticPromptDecision.FinishAndStartNew
                    : IsDeviceContinuityFollowPrompt(request)
                        ? AutomaticPromptDecision.SwitchNow
                        : AutomaticPromptDecision.No;

            var window = new AutomaticPromptWindow(request, defaultDecision);
            CancellationTokenRegistration cancellationRegistration = default;
            cancellationRegistration = cancellationToken.Register(() =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    if (!taskCompletionSource.Task.IsCompleted)
                    {
                        taskCompletionSource.TrySetResult(AutomaticPromptDecision.Cancelled);
                    }

                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                });
            });

            void Complete(AutomaticPromptDecision decision)
            {
                if (taskCompletionSource.TrySetResult(decision))
                {
                    cancellationRegistration.Dispose();
                }

                if (window.IsVisible)
                {
                    window.Close();
                }
            }

            window.DecisionMade += (_, decision) => Complete(decision);
            window.Closed += (_, _) =>
            {
                if (!taskCompletionSource.Task.IsCompleted)
                {
                    taskCompletionSource.TrySetResult(defaultDecision);
                    cancellationRegistration.Dispose();
                }
            };

            window.Show();
            window.Activate();
            window.Topmost = true;
        });

        return taskCompletionSource.Task;
    }

    private static bool IsUnknownPrompt(AutomaticPromptRequest request) =>
        string.Equals(request.PromptKind, "unknown_combined", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeviceContinuityFollowPrompt(AutomaticPromptRequest request) =>
        string.Equals(request.PromptKind, "device_continuity_follow_default", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeviceContinuityPinnedPrompt(AutomaticPromptRequest request) =>
        string.Equals(request.PromptKind, "device_continuity_pinned", StringComparison.OrdinalIgnoreCase);
}

internal sealed class AutomaticPromptWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly AutomaticPromptDecision _defaultDecision;
    private readonly TextBlock _countdownText;
    private int _remainingSeconds;

    public AutomaticPromptWindow(AutomaticPromptRequest request, AutomaticPromptDecision defaultDecision)
    {
        // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.ask
        // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.unknown-app
        _defaultDecision = defaultDecision;
        _remainingSeconds = Math.Max(0, request.TimeoutSeconds);

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
            Background = WpfBrushes.White;
        Title = "isTranscribe";

        var root = new Border
        {
            Background = WpfBrushes.White,
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(210, 214, 220)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20)
        };

        var stack = new StackPanel
        {
            Width = 380
        };

        stack.Children.Add(new TextBlock
        {
            Text = IsUnknownPrompt(request)
                ? LocalizationManager.Instance["Prompt_Title_UnknownMeeting"]
                : IsDeviceContinuityPrompt(request)
                    ? LocalizationManager.Instance["Prompt_Title_DeviceChanged"]
                    : LocalizationManager.Instance["Prompt_Title_KnownMeeting"],
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(28, 31, 37)),
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new TextBlock
        {
            Text = IsDeviceContinuityPrompt(request)
                ? $"{request.SourceApp} — {request.ProcessName}"
                : request.SourceApp,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(48, 54, 61)),
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new TextBlock
        {
            Text = request.ProcessName,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(102, 112, 122)),
            TextWrapping = TextWrapping.Wrap
        });

        _countdownText = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(133, 94, 0)),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_countdownText);
        UpdateCountdownText();

        var buttons = new UniformGrid
        {
            Margin = new Thickness(0, 16, 0, 0),
            Columns = IsUnknownPrompt(request) ? 2 : 1,
            Rows = IsUnknownPrompt(request)
                ? 2
                : IsDeviceContinuityPrompt(request)
                    ? 3
                    : 2
        };

        foreach (var option in BuildOptions(request))
        {
            var button = new WpfButton
            {
                Margin = new Thickness(4),
                Padding = new Thickness(14, 8, 14, 8),
                Content = option.Label,
                MinWidth = 140,
                Background = option.IsPrimary
                    ? new SolidColorBrush(WpfColor.FromRgb(32, 94, 229))
                    : new SolidColorBrush(WpfColor.FromRgb(242, 244, 247)),
                Foreground = option.IsPrimary ? WpfBrushes.White : new SolidColorBrush(WpfColor.FromRgb(34, 40, 49)),
                BorderBrush = option.IsPrimary
                    ? new SolidColorBrush(WpfColor.FromRgb(32, 94, 229))
                    : new SolidColorBrush(WpfColor.FromRgb(210, 214, 220))
            };
            button.Click += (_, _) => CloseWith(option.Decision);
            buttons.Children.Add(button);
        }

        stack.Children.Add(buttons);
        root.Child = stack;
        Content = root;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            UpdateCountdownText();
            if (_remainingSeconds <= 0)
            {
                CloseWith(_defaultDecision);
            }
        };

        Loaded += (_, _) =>
        {
            if (_remainingSeconds > 0)
            {
                _timer.Start();
            }
        };
        Closed += (_, _) => _timer.Stop();
    }

    public event EventHandler<AutomaticPromptDecision>? DecisionMade;

    private void CloseWith(AutomaticPromptDecision decision)
    {
        DecisionMade?.Invoke(this, decision);
    }

    private void UpdateCountdownText()
    {
        _countdownText.Text = _remainingSeconds > 0
            ? string.Format(LocalizationManager.Instance["Prompt_Countdown"], _remainingSeconds)
            : string.Empty;
    }

    private static bool IsUnknownPrompt(AutomaticPromptRequest request) =>
        string.Equals(request.PromptKind, "unknown_combined", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeviceContinuityPrompt(AutomaticPromptRequest request) =>
        string.Equals(request.PromptKind, "device_continuity_follow_default", StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.PromptKind, "device_continuity_pinned", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PromptOption> BuildOptions(AutomaticPromptRequest request) =>
        IsUnknownPrompt(request)
            ?
            [
                new PromptOption(LocalizationManager.Instance["Prompt_AddAndStart"], AutomaticPromptDecision.AddAndStart, IsPrimary: true),
                new PromptOption(LocalizationManager.Instance["Prompt_StartOnce"], AutomaticPromptDecision.StartOnce, IsPrimary: false),
                new PromptOption(LocalizationManager.Instance["Prompt_NotNow"], AutomaticPromptDecision.NotNow, IsPrimary: false),
                new PromptOption(LocalizationManager.Instance["Prompt_IgnoreThisApp"], AutomaticPromptDecision.IgnoreThisApp, IsPrimary: false)
            ]
            : IsDeviceContinuityPrompt(request)
                ?
                [
                    new PromptOption(LocalizationManager.Instance["Prompt_SwitchNow"], AutomaticPromptDecision.SwitchNow, IsPrimary: true),
                    new PromptOption(LocalizationManager.Instance["Prompt_FinishAndStartNew"], AutomaticPromptDecision.FinishAndStartNew, IsPrimary: false),
                    new PromptOption(LocalizationManager.Instance["Prompt_KeepCurrentIfPossible"], AutomaticPromptDecision.KeepCurrentIfPossible, IsPrimary: false)
                ]
            :
            [
                new PromptOption(LocalizationManager.Instance["Prompt_StartRecording"], AutomaticPromptDecision.StartRecording, IsPrimary: true),
                new PromptOption(LocalizationManager.Instance["Prompt_No"], AutomaticPromptDecision.No, IsPrimary: false)
            ];

    private sealed record PromptOption(string Label, AutomaticPromptDecision Decision, bool IsPrimary);
}

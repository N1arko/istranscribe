using System.Globalization;
using Avalonia.Controls;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Services;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class AskPromptTests
{
    [Fact]
    public void Open_prompt_relocalizes_every_bound_label_without_changing_semantic_state()
    {
        var prompt = Prompt("localized", DateTimeOffset.UtcNow.AddMinutes(1));
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: prompt));
        var strings = new AskTestLocalizationService();
        using var viewModel = new AskPromptViewModel(
            runtime,
            strings,
            prompt,
            TimeProvider.System,
            showAdvancedDiagnostics: true);
        var localizedProperties = new[]
        {
            nameof(viewModel.Title),
            nameof(viewModel.AppLine),
            nameof(viewModel.CountdownText),
            nameof(viewModel.CountdownAutomationText),
            nameof(viewModel.RecordActionText),
            nameof(viewModel.SkipActionText),
            nameof(viewModel.MoreActionText),
            nameof(viewModel.IgnoreApplicationText),
            nameof(viewModel.WhyShownText),
            nameof(viewModel.AutomationName),
            nameof(viewModel.DecisionReasonText),
            nameof(viewModel.ResolutionErrorText)
        };
        var before = LocalizedValues(viewModel);
        var candidateId = viewModel.CandidateId;
        var expiresAtUtc = viewModel.ExpiresAtUtc;
        var remaining = viewModel.Remaining;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } propertyName)
            {
                changed.Add(propertyName);
            }
        };

        strings.SetLanguage(UiLanguage.English);

        var after = LocalizedValues(viewModel);
        Assert.All(localizedProperties, property => Assert.Contains(property, changed));
        Assert.All(
            before.Keys,
            property => Assert.NotEqual(before[property], after[property]));
        Assert.Equal(candidateId, viewModel.CandidateId);
        Assert.Equal(expiresAtUtc, viewModel.ExpiresAtUtc);
        Assert.Equal(remaining, viewModel.Remaining);
        Assert.True(viewModel.ShowAdvancedDiagnostics);
        Assert.False(viewModel.IsResolving);
        Assert.False(viewModel.IsResolved);
        Assert.True(viewModel.IsInteractionEnabled);
        Assert.False(viewModel.HasResolutionError);
        Assert.Empty(runtime.PromptResolutions);
    }

    [Fact]
    public async Task Expired_prompt_resolves_as_skip_once_and_marks_timeout()
    {
        var prompt = Prompt("expired", DateTimeOffset.UtcNow.AddSeconds(-1));
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: prompt));
        using var viewModel = new AskPromptViewModel(
            runtime,
            new FakeLocalizationService(),
            prompt,
            TimeProvider.System);
        AskPromptResolvedEventArgs? resolution = null;
        viewModel.Resolved += (_, args) => resolution = args;

        await viewModel.RunCountdownAsync();

        var runtimeResolution = Assert.Single(runtime.PromptResolutions);
        Assert.Equal("expired", runtimeResolution.CandidateId);
        Assert.Equal(MeetingPromptUserAction.Skip, runtimeResolution.Action);
        Assert.NotNull(resolution);
        Assert.Equal(MeetingPromptUserAction.Skip, resolution.Action);
        Assert.True(resolution.IsTimeout);
        Assert.True(viewModel.IsResolved);
        Assert.False(viewModel.IsInteractionEnabled);
    }

    [Fact]
    public async Task Competing_decisions_submit_only_the_first_action()
    {
        var prompt = Prompt("race", DateTimeOffset.UtcNow.AddMinutes(1));
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: prompt));
        using var viewModel = new AskPromptViewModel(
            runtime,
            new FakeLocalizationService(),
            prompt,
            TimeProvider.System);

        await Task.WhenAll(viewModel.RecordAsync(), viewModel.SkipAsync());

        var resolution = Assert.Single(runtime.PromptResolutions);
        Assert.Equal(MeetingPromptUserAction.Record, resolution.Action);
    }

    /// <summary>
    /// @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
    /// </summary>
    [Fact]
    public async Task Record_resolution_survives_runtime_clearing_the_pending_prompt()
    {
        var prompt = Prompt("record", DateTimeOffset.UtcNow.AddMinutes(1));
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: prompt));
        var resolutionCompleted = false;
        runtime.PromptResolutionHandler = async (_, action, cancellationToken) =>
        {
            Assert.Equal(MeetingPromptUserAction.Record, action);
            runtime.Publish(SnapshotFactory.Create(ApplicationActivityState.Suspected));
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            resolutionCompleted = true;
        };
        FakeAskPromptSurface? surface = null;
        await using var controller = new AskPromptController(
            runtime,
            new FakeLocalizationService(),
            timeProvider: TimeProvider.System,
            surfaceFactory: viewModel => surface = new FakeAskPromptSurface(viewModel),
            dispatch: action => action());
        controller.Start();
        var visiblePrompt = Assert.IsType<AskPromptViewModel>(controller.CurrentPrompt);

        await visiblePrompt.RecordAsync();

        Assert.True(resolutionCompleted);
        Assert.Equal(
            [("record", MeetingPromptUserAction.Record)],
            runtime.PromptResolutions);
        Assert.NotNull(surface);
        Assert.Equal(1, surface.CloseCalls);
        Assert.False(controller.IsPromptVisible);
    }

    [Fact]
    public async Task Controller_reuses_same_candidate_and_never_keeps_two_surfaces()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create());
        var surfaces = new List<FakeAskPromptSurface>();
        await using var controller = new AskPromptController(
            runtime,
            new FakeLocalizationService(),
            timeProvider: TimeProvider.System,
            surfaceFactory: viewModel =>
            {
                var surface = new FakeAskPromptSurface(viewModel);
                surfaces.Add(surface);
                return surface;
            },
            dispatch: action => action());
        var first = Prompt("one", DateTimeOffset.UtcNow.AddHours(1));

        controller.ApplySnapshot(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: first));
        controller.ApplySnapshot(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: first with { SourceLabel = "Updated Zoom" }));

        Assert.Single(surfaces);
        Assert.True(controller.IsPromptVisible);
        Assert.Equal("String.App.Zoom", controller.CurrentPrompt?.SourceLabel);
        Assert.Equal(1, surfaces[0].ShowCalls);
        Assert.Equal(0, surfaces[0].CloseCalls);

        controller.ApplySnapshot(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: Prompt("two", DateTimeOffset.UtcNow.AddHours(1))));

        Assert.Equal(2, surfaces.Count);
        Assert.Equal(1, surfaces[0].CloseCalls);
        Assert.Equal("two", controller.CurrentPrompt?.CandidateId);
        Assert.Equal(1, surfaces[1].ShowCalls);

        controller.ApplySnapshot(SnapshotFactory.Create());

        Assert.False(controller.IsPromptVisible);
        Assert.Equal(1, surfaces[1].CloseCalls);
    }

    [Fact]
    public async Task User_closing_surface_resolves_visible_prompt_as_skip()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create());
        FakeAskPromptSurface? surface = null;
        await using var controller = new AskPromptController(
            runtime,
            new FakeLocalizationService(),
            timeProvider: TimeProvider.System,
            surfaceFactory: viewModel => surface = new FakeAskPromptSurface(viewModel),
            dispatch: action => action());
        controller.ApplySnapshot(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: Prompt("closed", DateTimeOffset.UtcNow.AddHours(1))));

        Assert.NotNull(surface);
        surface.RaiseClosed();

        await WaitUntilAsync(() => runtime.PromptResolutions.Count == 1);
        var resolution = Assert.Single(runtime.PromptResolutions);
        Assert.Equal(("closed", MeetingPromptUserAction.Skip), resolution);
    }

    private static MeetingPromptSnapshot Prompt(string candidateId, DateTimeOffset expiresAtUtc) => new(
        candidateId,
        "zoom",
        "Zoom",
        DateTimeOffset.UtcNow,
        expiresAtUtc,
        ConfidenceScore: 82,
        DecisionReasons: ["application", "conversation"]);

    private static IReadOnlyDictionary<string, string> LocalizedValues(AskPromptViewModel viewModel) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(viewModel.Title)] = viewModel.Title,
            [nameof(viewModel.AppLine)] = viewModel.AppLine,
            [nameof(viewModel.CountdownText)] = viewModel.CountdownText,
            [nameof(viewModel.CountdownAutomationText)] = viewModel.CountdownAutomationText,
            [nameof(viewModel.RecordActionText)] = viewModel.RecordActionText,
            [nameof(viewModel.SkipActionText)] = viewModel.SkipActionText,
            [nameof(viewModel.MoreActionText)] = viewModel.MoreActionText,
            [nameof(viewModel.IgnoreApplicationText)] = viewModel.IgnoreApplicationText,
            [nameof(viewModel.WhyShownText)] = viewModel.WhyShownText,
            [nameof(viewModel.AutomationName)] = viewModel.AutomationName,
            [nameof(viewModel.DecisionReasonText)] = viewModel.DecisionReasonText,
            [nameof(viewModel.ResolutionErrorText)] = viewModel.ResolutionErrorText
        };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeAskPromptSurface(AskPromptViewModel viewModel) : IAskPromptSurface
    {
        public event EventHandler? Closed;

        public AskPromptViewModel ViewModel { get; } = viewModel;

        public int ShowCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public void ShowNear(Window? placementOwner) => ShowCalls++;

        public void CloseWithoutResolution() => CloseCalls++;

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AskTestLocalizationService : ILocalizationService
    {
        public UiLanguage CurrentLanguage { get; private set; } = UiLanguage.Russian;

        public CultureInfo CurrentCulture => CurrentLanguage == UiLanguage.English
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("ru-RU");

        public event EventHandler? LanguageChanged;

        public void Attach(Avalonia.Application application) =>
            ArgumentNullException.ThrowIfNull(application);

        public void SetLanguage(UiLanguage language)
        {
            if (CurrentLanguage == language)
            {
                return;
            }

            CurrentLanguage = language;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        public string Get(string resourceKey)
        {
            var prefix = CurrentLanguage == UiLanguage.English ? "en" : "ru";
            return resourceKey switch
            {
                "String.Ask.AppLine.Format" => $"{prefix}:app:{{0}}",
                "String.Ask.Countdown.Format" => $"{prefix}:countdown:{{0}}",
                "String.Ask.Countdown.Accessible.One" => $"{prefix}:one:{{0}}",
                "String.Ask.Countdown.Accessible.Few" => $"{prefix}:few:{{0}}",
                "String.Ask.Countdown.Accessible.Many" => $"{prefix}:many:{{0}}",
                _ => $"{prefix}:{resourceKey}"
            };
        }

        public string Format(string resourceKey, params object?[] arguments) =>
            string.Format(CurrentCulture, Get(resourceKey), arguments);
    }
}

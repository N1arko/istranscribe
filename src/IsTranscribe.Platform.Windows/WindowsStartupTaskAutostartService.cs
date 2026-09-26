using System.Runtime.Versioning;
using IsTranscribe.Core.Platform;
using Windows.ApplicationModel;

namespace IsTranscribe.Platform.Windows;

internal enum WindowsStartupTaskState
{
    Disabled,
    DisabledByUser,
    Enabled,
    DisabledByPolicy,
    EnabledByPolicy
}

internal interface IWindowsStartupTaskAdapter
{
    ValueTask<WindowsStartupTaskState> GetStateAsync(
        string taskId,
        CancellationToken cancellationToken);

    ValueTask<WindowsStartupTaskState> RequestEnableAsync(
        string taskId,
        CancellationToken cancellationToken);

    ValueTask<WindowsStartupTaskState> DisableAsync(
        string taskId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Package-aware launch-at-login registration backed by the Windows StartupTask contract.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#scope.in
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupTaskAutostartService : IAutostartService
{
    internal const string TaskId = "isTranscribeStartup";

    private readonly IWindowsStartupTaskAdapter _adapter;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public WindowsStartupTaskAutostartService()
        : this(new WindowsStartupTaskAdapter())
    {
    }

    internal WindowsStartupTaskAutostartService(IWindowsStartupTaskAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public async ValueTask<AutostartRegistrationState> GetStateAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _adapter
                .GetStateAsync(TaskId, cancellationToken)
                .ConfigureAwait(false);
            return ToRegistrationState(state);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<AutostartRegistrationState> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _adapter
                .GetStateAsync(TaskId, cancellationToken)
                .ConfigureAwait(false);
            if (enabled)
            {
                return await EnableAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return await DisableAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<AutostartRegistrationState> EnableAsync(
        WindowsStartupTaskState state,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case WindowsStartupTaskState.Enabled:
            case WindowsStartupTaskState.EnabledByPolicy:
                return ToRegistrationState(state);
            case WindowsStartupTaskState.DisabledByUser:
                throw WindowsStartupTaskStateBlockedException.For(state);
            case WindowsStartupTaskState.DisabledByPolicy:
                throw WindowsStartupTaskStateBlockedException.For(state);
            case WindowsStartupTaskState.Disabled:
                var requestedState = await _adapter
                    .RequestEnableAsync(TaskId, cancellationToken)
                    .ConfigureAwait(false);
                return EnsureRequestedState(requestedState);
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Windows startup task state.");
        }
    }

    private async ValueTask<AutostartRegistrationState> DisableAsync(
        WindowsStartupTaskState state,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case WindowsStartupTaskState.Disabled:
            case WindowsStartupTaskState.DisabledByUser:
            case WindowsStartupTaskState.DisabledByPolicy:
                return ToRegistrationState(state);
            case WindowsStartupTaskState.EnabledByPolicy:
                throw WindowsStartupTaskStateBlockedException.For(state);
            case WindowsStartupTaskState.Enabled:
                var disabledState = await _adapter
                    .DisableAsync(TaskId, cancellationToken)
                    .ConfigureAwait(false);
                if (IsEnabled(disabledState))
                {
                    throw new InvalidOperationException(
                        "Windows did not disable the isTranscribe startup task.");
                }

                return ToRegistrationState(disabledState);
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Windows startup task state.");
        }
    }

    private static AutostartRegistrationState EnsureRequestedState(WindowsStartupTaskState state) => state switch
    {
        WindowsStartupTaskState.Enabled or WindowsStartupTaskState.EnabledByPolicy =>
            ToRegistrationState(state),
        WindowsStartupTaskState.DisabledByUser => throw WindowsStartupTaskStateBlockedException.For(state),
        WindowsStartupTaskState.DisabledByPolicy => throw WindowsStartupTaskStateBlockedException.For(state),
        WindowsStartupTaskState.Disabled => throw new InvalidOperationException(
            "Windows did not enable the isTranscribe startup task."),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Windows startup task state.")
    };

    private static AutostartRegistrationState ToRegistrationState(WindowsStartupTaskState state) => state switch
    {
        WindowsStartupTaskState.Disabled => new(false),
        WindowsStartupTaskState.DisabledByUser => new(false, AutostartControlConstraint.DisabledByUser),
        WindowsStartupTaskState.Enabled => new(true),
        WindowsStartupTaskState.DisabledByPolicy => new(false, AutostartControlConstraint.DisabledByPolicy),
        WindowsStartupTaskState.EnabledByPolicy => new(true, AutostartControlConstraint.EnabledByPolicy),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Windows startup task state.")
    };

    private static bool IsEnabled(WindowsStartupTaskState state) =>
        state is WindowsStartupTaskState.Enabled or WindowsStartupTaskState.EnabledByPolicy;
}

internal sealed class WindowsStartupTaskStateBlockedException : InvalidOperationException
{
    private WindowsStartupTaskStateBlockedException(
        WindowsStartupTaskState state,
        string message)
        : base(message)
    {
        State = state;
    }

    public WindowsStartupTaskState State { get; }

    public static WindowsStartupTaskStateBlockedException For(WindowsStartupTaskState state) => state switch
    {
        WindowsStartupTaskState.DisabledByUser => new(
            state,
            "Windows Startup apps has disabled isTranscribe for this user. Re-enable it in Windows Settings."),
        WindowsStartupTaskState.DisabledByPolicy => new(
            state,
            "A Windows policy prevents isTranscribe from starting at sign-in."),
        WindowsStartupTaskState.EnabledByPolicy => new(
            state,
            "A Windows policy requires isTranscribe to start at sign-in."),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The startup state is not policy-blocked.")
    };

    public AutostartRegistrationState ToRegistrationState() =>
        State switch
        {
            WindowsStartupTaskState.DisabledByUser =>
                new(false, AutostartControlConstraint.DisabledByUser),
            WindowsStartupTaskState.DisabledByPolicy =>
                new(false, AutostartControlConstraint.DisabledByPolicy),
            WindowsStartupTaskState.EnabledByPolicy =>
                new(true, AutostartControlConstraint.EnabledByPolicy),
            _ => throw new ArgumentOutOfRangeException(
                nameof(State),
                State,
                "The startup state is not policy-blocked.")
        };
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsStartupTaskAdapter : IWindowsStartupTaskAdapter
{
    [SupportedOSPlatform("windows10.0.14393.0")]
    public async ValueTask<WindowsStartupTaskState> GetStateAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = await GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        return Map(task.State);
    }

    [SupportedOSPlatform("windows10.0.14393.0")]
    public async ValueTask<WindowsStartupTaskState> RequestEnableAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = await GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        var state = await task.RequestEnableAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return Map(state);
    }

    [SupportedOSPlatform("windows10.0.14393.0")]
    public async ValueTask<WindowsStartupTaskState> DisableAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = await GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        task.Disable();
        cancellationToken.ThrowIfCancellationRequested();
        return Map(task.State);
    }

    [SupportedOSPlatform("windows10.0.14393.0")]
    private static async ValueTask<StartupTask> GetTaskAsync(
        string taskId,
        CancellationToken cancellationToken) =>
        await StartupTask.GetAsync(taskId).AsTask(cancellationToken).ConfigureAwait(false);

    private static WindowsStartupTaskState Map(StartupTaskState state) => (int)state switch
    {
        0 => WindowsStartupTaskState.Disabled,
        1 => WindowsStartupTaskState.DisabledByUser,
        2 => WindowsStartupTaskState.Enabled,
        3 => WindowsStartupTaskState.DisabledByPolicy,
        4 => WindowsStartupTaskState.EnabledByPolicy,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Windows startup task state.")
    };
}

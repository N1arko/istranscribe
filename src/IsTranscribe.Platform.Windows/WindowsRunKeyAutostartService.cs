using System.Runtime.Versioning;
using IsTranscribe.Core.Platform;
using Microsoft.Win32;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Per-user Windows launch-at-login registration backed by the supported Run key.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyAutostartService : IAutostartService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "isTranscribe";
    private const int MaximumRunCommandLength = 260;

    private readonly IWindowsRunKeyStore _store;
    private readonly Func<string?> _executablePathProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public WindowsRunKeyAutostartService()
        : this(new WindowsRegistryRunKeyStore(), static () => Environment.ProcessPath)
    {
    }

    internal WindowsRunKeyAutostartService(
        IWindowsRunKeyStore store,
        Func<string?> executablePathProvider)
    {
        _store = store;
        _executablePathProvider = executablePathProvider;
    }

    public async ValueTask<AutostartRegistrationState> GetStateAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var expectedCommand = ResolveExpectedCommand();
            var actualCommand = _store.Read(ValueName);
            return new AutostartRegistrationState(CommandMatches(actualCommand, expectedCommand));
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
            var expectedCommand = ResolveExpectedCommand();
            var actualCommand = _store.Read(ValueName);
            if (enabled)
            {
                if (!CommandMatches(actualCommand, expectedCommand))
                {
                    _store.Write(ValueName, expectedCommand);
                }
            }
            else if (actualCommand is not null)
            {
                _store.Delete(ValueName);
            }

            cancellationToken.ThrowIfCancellationRequested();
            actualCommand = _store.Read(ValueName);
            var actualEnabled = CommandMatches(actualCommand, expectedCommand);
            if (actualEnabled != enabled)
            {
                throw new InvalidOperationException("Windows did not retain the requested launch-at-login state.");
            }

            return new AutostartRegistrationState(actualEnabled);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal static string BuildCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("The current executable path is unavailable.");
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The current executable path contains an unsupported quote.");
        }

        var command = $"\"{fullPath}\" --autostart";
        if (command.Length > MaximumRunCommandLength)
        {
            throw new InvalidOperationException("The launch-at-login command exceeds the Windows Run key limit.");
        }

        return command;
    }

    private string ResolveExpectedCommand() => BuildCommand(_executablePathProvider() ?? string.Empty);

    private static bool CommandMatches(string? actual, string expected) =>
        string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

internal interface IWindowsRunKeyStore
{
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistryRunKeyStore : IWindowsRunKeyStore
{
    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKeyAutostartService.RunKeyPath);
        return key?.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
    }

    public void Write(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            WindowsRunKeyAutostartService.RunKeyPath,
            writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            WindowsRunKeyAutostartService.RunKeyPath,
            writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

using System.Diagnostics;
using IsTranscribe.Application.Platform;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Key-addressable generic-password vault backed by the current user's macOS Keychain.
/// Secret values are delivered to the security tool through redirected standard input.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// </remarks>
public sealed class MacOSKeychainSecretVault : ISecretVault
{
    private const string ServiceName = "isTranscribe";
    private const int ItemNotFoundExitCode = 44;

    private readonly IKeychainCommandRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MacOSKeychainSecretVault()
        : this(new SecurityToolRunner())
    {
    }

    internal MacOSKeychainSecretVault(IKeychainCommandRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var account = NormalizeKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _runner.RunAsync(
                ["find-generic-password", "-a", account, "-s", ServiceName, "-w"],
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == ItemNotFoundExitCode)
            {
                return null;
            }

            EnsureSuccess(result);
            return result.StandardOutput.TrimEnd('\r', '\n');
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        var account = NormalizeKey(key);
        if (value is not null && (value.Length == 0 || value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException(
                "A Keychain value must be non-empty and contain no line breaks.",
                nameof(value));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (value is null)
            {
                var deletion = await _runner.RunAsync(
                    ["delete-generic-password", "-a", account, "-s", ServiceName],
                    standardInput: null,
                    cancellationToken).ConfigureAwait(false);
                if (deletion.ExitCode != ItemNotFoundExitCode)
                {
                    EnsureSuccess(deletion);
                }

                return;
            }

            var update = await _runner.RunAsync(
                ["add-generic-password", "-a", account, "-s", ServiceName, "-U", "-w"],
                value,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(update);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A secret vault key is required.", nameof(key));
        }

        var normalized = key.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The secret vault key is invalid.", nameof(key));
        }

        return normalized;
    }

    private static void EnsureSuccess(KeychainCommandResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The macOS Keychain operation failed with exit code {result.ExitCode}.");
        }
    }
}

internal sealed record KeychainCommandResult(
    int ExitCode,
    string StandardOutput);

internal interface IKeychainCommandRunner
{
    ValueTask<KeychainCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken);
}

internal sealed class SecurityToolRunner : IKeychainCommandRunner
{
    public async ValueTask<KeychainCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The macOS Keychain tool could not be started.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        await standardError.ConfigureAwait(false);
        return new KeychainCommandResult(process.ExitCode, await standardOutput.ConfigureAwait(false));
    }
}

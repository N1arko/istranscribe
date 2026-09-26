using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </summary>
public sealed class MacOSKeychainSecretVaultTests
{
    [Fact]
    public async Task ReadUsesServiceAndAccountWithoutSupplyingSecretArguments()
    {
        var runner = new RecordingRunner(new KeychainCommandResult(0, "synthetic-value\n"));
        var vault = new MacOSKeychainSecretVault(runner);

        var value = await vault.ReadAsync("transcription.remote.groq.api-key", CancellationToken.None);

        Assert.Equal("synthetic-value", value);
        Assert.Equal(
            ["find-generic-password", "-a", "transcription.remote.groq.api-key", "-s", "isTranscribe", "-w"],
            Assert.Single(runner.Calls).Arguments);
        Assert.Null(runner.Calls[0].StandardInput);
    }

    [Fact]
    public async Task WritePassesValueOnlyThroughRedirectedStandardInput()
    {
        const string syntheticValue = "synthetic-value-kept-off-process-arguments";
        var runner = new RecordingRunner(new KeychainCommandResult(0, string.Empty));
        var vault = new MacOSKeychainSecretVault(runner);

        await vault.WriteAsync(
            "transcription.remote.openrouter.api-key",
            syntheticValue,
            CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(
            ["add-generic-password", "-a", "transcription.remote.openrouter.api-key", "-s", "isTranscribe", "-U", "-w"],
            call.Arguments);
        Assert.DoesNotContain(syntheticValue, call.Arguments);
        Assert.Equal(syntheticValue, call.StandardInput);
    }

    [Fact]
    public async Task DeleteAndMissingReadAreIdempotent()
    {
        var runner = new RecordingRunner(
            new KeychainCommandResult(44, string.Empty),
            new KeychainCommandResult(44, string.Empty));
        var vault = new MacOSKeychainSecretVault(runner);

        Assert.Null(await vault.ReadAsync("transcription.remote.groq.api-key", CancellationToken.None));
        await vault.WriteAsync("transcription.remote.groq.api-key", null, CancellationToken.None);

        Assert.Equal("delete-generic-password", runner.Calls[1].Arguments[0]);
    }

    [Fact]
    public async Task UnexpectedToolFailureUsesBoundedErrorWithoutCapturedOutput()
    {
        const string untrustedOutput = "output that must stay out of the exception";
        var vault = new MacOSKeychainSecretVault(
            new RecordingRunner(new KeychainCommandResult(36, untrustedOutput)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vault.ReadAsync("transcription.remote.groq.api-key", CancellationToken.None).AsTask());

        Assert.Contains("36", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedOutput, exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingRunner(params KeychainCommandResult[] results) : IKeychainCommandRunner
    {
        private readonly Queue<KeychainCommandResult> _results = new(results);

        public List<Call> Calls { get; } = [];

        public ValueTask<KeychainCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(arguments.ToArray(), standardInput));
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed record Call(IReadOnlyList<string> Arguments, string? StandardInput);
}

using IsTranscribe.DetectionAcceptance;

// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
try
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("FEAT-011 acceptance currently supports Windows x64 only.");
        return 78;
    }

    if (args.Length > 0 && string.Equals(args[0], "aggregate", StringComparison.OrdinalIgnoreCase))
    {
        var output = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "acceptance",
            "FEAT-011",
            "windows-x64");
        if (args.Length == 3 && string.Equals(args[1], "--output", StringComparison.Ordinal))
        {
            output = Path.GetFullPath(args[2]);
        }
        else if (args.Length != 1)
        {
            throw new ArgumentException("Usage: aggregate [--output <directory>]");
        }

        var matrix = await new LiveEvidenceMatrixBuilder()
            .BuildAndWriteAsync(output, CancellationToken.None);
        var compatibility = await new CompatibilityEvidenceIndexBuilder()
            .BuildAndWriteAsync(output, matrix, CancellationToken.None);
        Console.WriteLine($"matrix={matrix.Status.ToString().ToLowerInvariant()}");
        Console.WriteLine($"artifact={Path.Combine(output, "live-matrix.json")}");
        Console.WriteLine($"compatibility={compatibility.Status.ToString().ToLowerInvariant()}");
        Console.WriteLine($"compatibility_artifact={Path.Combine(output, "compatibility-index.json")}");
        return CompatibilityAggregateExitPolicy.GetExitCode(matrix.Status, compatibility.Status);
    }

    var options = AcceptanceOptions.Parse(args);
    var result = await new AcceptanceRunner().RunAsync(options, CancellationToken.None);
    Console.WriteLine($"outcome={result.Evidence.Outcome.Status.ToString().ToLowerInvariant()}");
    Console.WriteLine($"reason={result.Evidence.Outcome.ReasonCode ?? "none"}");
    Console.WriteLine($"artifact={result.ArtifactDirectory}");
    return result.Evidence.Outcome.Status switch
    {
        RunOutcomeStatus.Passed => 0,
        RunOutcomeStatus.Failed => 1,
        RunOutcomeStatus.Blocked or RunOutcomeStatus.NotRun => 2,
        _ => 1
    };
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(
        "Usage: --profile <id> --surface <desktop|browser> [--client-process <name.exe>] [--operator-ready] "
        + "[--timeout-seconds <1..60>] [--output <directory>]");
    return 64;
}

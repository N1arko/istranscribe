namespace IsTranscribe.VisualReview;

/// <summary>
/// Resolves the artifact root without relying on the caller's current directory.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// </remarks>
internal sealed record ReviewCommandLine(string OutputRoot, string? StoreListingOutputRoot)
{
    public bool IsStoreListingCapture => StoreListingOutputRoot is not null;

    public static ReviewCommandLine Parse(IReadOnlyList<string> args)
    {
        string? requestedRoot = null;
        string? requestedStoreListingRoot = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!string.Equals(argument, "--output", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(argument, "--store-listing-output", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unknown argument '{argument}'.");
            }

            if (++index >= args.Count)
            {
                throw new ArgumentException($"{argument} requires a path.");
            }

            if (string.Equals(argument, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (requestedRoot is not null)
                {
                    throw new ArgumentException("--output can only be specified once.");
                }

                requestedRoot = args[index];
            }
            else
            {
                if (requestedStoreListingRoot is not null)
                {
                    throw new ArgumentException("--store-listing-output can only be specified once.");
                }

                requestedStoreListingRoot = args[index];
            }
        }

        if (requestedRoot is not null && requestedStoreListingRoot is not null)
        {
            throw new ArgumentException(
                "--output and --store-listing-output select separate artifact suites and cannot be combined.");
        }

        var root = requestedRoot is null
            ? FindRepositoryRoot(AppContext.BaseDirectory)
            : Path.GetFullPath(requestedRoot);
        var outputRoot = requestedRoot is null
            ? Path.Combine(root, "artifacts", "visual-review", "FEAT-010.A")
            : root;
        var storeListingOutputRoot = requestedStoreListingRoot is null
            ? null
            : Path.GetFullPath(requestedStoreListingRoot);
        return new ReviewCommandLine(outputRoot, storeListingOutputRoot);
    }

    private static string FindRepositoryRoot(string startingPath)
    {
        var directory = new DirectoryInfo(startingPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "isTranscribe.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate isTranscribe.sln from the tool directory.");
    }
}

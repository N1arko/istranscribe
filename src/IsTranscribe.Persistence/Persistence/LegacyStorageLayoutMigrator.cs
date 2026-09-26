using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Persistence;

public sealed class LegacyStorageLayoutMigrator(LocalAppPaths paths)
{
    private readonly LocalAppPaths _paths = paths;

    public void MigrateIfNeeded()
    {
        if (!Directory.Exists(_paths.LegacyBootstrapRootDirectory))
        {
            return;
        }

        MoveLegacyDirectory("config");
        MoveLegacyDirectory("data");
        MoveLegacyDirectory("temp");
        MoveLegacyDirectory("logs");
    }

    private void MoveLegacyDirectory(string name)
    {
        var source = Path.Combine(_paths.LegacyBootstrapRootDirectory, name);
        var destination = Path.Combine(_paths.RootDirectory, name);

        if (!Directory.Exists(source) || Directory.Exists(destination))
        {
            return;
        }

        Directory.Move(source, destination);
    }
}

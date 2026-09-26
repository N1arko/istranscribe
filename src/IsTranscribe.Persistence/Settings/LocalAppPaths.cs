namespace IsTranscribe.Host.Settings;

public sealed class LocalAppPaths
{
    private readonly string _applicationName;
    private readonly string _rootDirectory;

    public LocalAppPaths(string applicationName)
        : this(applicationName, rootDirectoryOverride: null)
    {
    }

    public LocalAppPaths(string applicationName, string? rootDirectoryOverride)
    {
        _applicationName = applicationName;
        _rootDirectory = rootDirectoryOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                applicationName);
    }

    // @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#persistence-and-migration

    public string RootDirectory => _rootDirectory;

    public string LegacyBootstrapRootDirectory => Path.Combine(_rootDirectory, "AppData");

    public string ConfigDirectory => Path.Combine(_rootDirectory, "config");

    public string DataDirectory => Path.Combine(_rootDirectory, "data");

    public string TempDirectory => Path.Combine(_rootDirectory, "temp");

    public string LogsDirectory => Path.Combine(_rootDirectory, "logs");

    public string SettingsFilePath => Path.Combine(ConfigDirectory, "settings.json");

    public string SecretsFilePath => Path.Combine(ConfigDirectory, "secrets.bin");

    public string DatabaseFilePath => Path.Combine(DataDirectory, "app.db");

    public string HostLogFilePath => Path.Combine(LogsDirectory, "host-bootstrap.log");

    public string DocumentsRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        _applicationName);

    public string DefaultRecordingsDirectory => Path.Combine(DocumentsRootDirectory, "Recordings");

    public string DefaultTranscriptsDirectory => Path.Combine(DocumentsRootDirectory, "Transcripts");

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#file-layout.app-owned
    public void EnsureAppOwnedDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

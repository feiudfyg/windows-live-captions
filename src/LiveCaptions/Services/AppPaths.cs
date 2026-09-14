namespace LiveCaptions.Services;

/// <summary>
/// Resolves the writable data root (models, settings, logs, crash dumps).
///
/// When running from a source checkout the repository's <c>data/</c> folder is
/// used so nothing large ends up on the system drive; published builds fall
/// back to <c>%LOCALAPPDATA%\LiveCaptions</c>.
/// </summary>
internal static class AppPaths
{
    public static string DataRoot { get; } = Resolve();

    public static string ModelsDirectory => Path.Combine(DataRoot, "models");

    public static string SettingsPath => Path.Combine(DataRoot, "settings.json");

    public static string LogPath => Path.Combine(DataRoot, "livecaptions.log");

    public static string CrashDumpsDirectory => Path.Combine(DataRoot, "crashes");

    private static string Resolve()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiveCaptions.slnx")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return Path.Combine(directory.FullName, "data");
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptions");
    }
}

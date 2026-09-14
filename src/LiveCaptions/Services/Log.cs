namespace LiveCaptions.Services;

/// <summary>
/// Minimal append-only diagnostic log (%LOCALAPPDATA%\LiveCaptions\livecaptions.log).
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = AppPaths.LogPath;

    public static void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Gate)
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 4_000_000)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}

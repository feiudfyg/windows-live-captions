using System.Text.Json;
using LiveCaptions.Models;

namespace LiveCaptions.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public AppSettings Settings { get; private set; } = new();

    public string SettingsPath => AppPaths.SettingsPath;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            // Keep the broken file for inspection instead of silently resetting.
            Log.Write($"[settings] load failed ({ex.Message}); using defaults");
            try
            {
                File.Move(SettingsPath, SettingsPath + ".broken", overwrite: true);
            }
            catch
            {
                // Best effort.
            }

            Settings = new AppSettings();
        }

        Settings.Validate();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            // Write-then-rename: a crash mid-save must never truncate the settings.
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Options));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Settings persistence is best-effort; never crash the app over it.
            Log.Write($"[settings] save failed: {ex.Message}");
        }
    }

    /// <summary>Replace the active settings object and persist it. The caller keeps
    /// its own copy: the settings window keeps mutating its working object for live
    /// previews after saving, and those edits must not leak into the running app.</summary>
    public void Update(AppSettings settings)
    {
        Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Options), Options)
                   ?? settings;
        Save();
    }
}

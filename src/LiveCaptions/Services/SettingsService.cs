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

    public string SettingsPath => Path.Combine(Settings.SettingsDirectory, "settings.json");

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
        catch
        {
            Settings = new AppSettings();
        }

        Settings.Validate();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, Options));
        }
        catch
        {
            // Settings persistence is best-effort; never crash the app over it.
        }
    }

    /// <summary>Replace the active settings object and persist it.</summary>
    public void Update(AppSettings settings)
    {
        Settings = settings;
        Save();
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DimDock.Models;

namespace DimDock.Services;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService(string? customSettingsPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customSettingsPath))
        {
            _settingsPath = customSettingsPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _settingsPath = Path.Combine(appData, "DimDock", "settings.json");
        }

        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                var parsed = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                if (parsed is not null)
                {
                    Current = parsed.Normalize();
                    return Current;
                }
            }
        }
        catch (Exception ex)
        {
            // Fall back to clean defaults if settings file is missing or corrupted.
            AppLogger.Warn($"Failed to load settings from '{_settingsPath}', using defaults", ex);
        }

        Current = new AppSettings().Normalize();
        return Current;
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(Current.Normalize(), AppSettingsJsonContext.Default.AppSettings);
            string tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Avoid crashing tray utility on transient IO lock.
            AppLogger.Warn($"Failed to save settings to '{_settingsPath}'", ex);
        }
    }
}

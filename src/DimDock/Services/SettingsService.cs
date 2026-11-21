using System.IO;
using System.Text.Json;
using DimDock.Models;

namespace DimDock.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

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
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (parsed is not null)
                {
                    Current = parsed.Normalize();
                    return Current;
                }
            }
        }
        catch
        {
            // Fall back to clean defaults if settings file is missing or corrupted.
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

            string json = JsonSerializer.Serialize(Current.Normalize(), JsonOptions);
            string tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            // Avoid crashing tray utility on transient IO lock.
        }
    }
}

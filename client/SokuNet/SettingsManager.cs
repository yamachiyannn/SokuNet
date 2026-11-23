using System.Text.Json;

namespace SokuNet;

internal sealed class AppSettings
{
    public bool PrivateMode { get; set; }
    public bool NotificationSoundEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string RelayUrl { get; set; } = string.Empty;
    public string RoomCode { get; set; } = string.Empty;
}

internal sealed class SettingsManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Keep the file beside sokunet.exe so it can be found and edited in Explorer.
    public string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "config.json");
    public string AppDirectory => AppContext.BaseDirectory;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), JsonOptions);
            loaded ??= new AppSettings();
            loaded.DisplayName = CleanSetting(loaded.DisplayName, string.Empty, 24);
            loaded.RelayUrl ??= string.Empty;
            loaded.RoomCode ??= string.Empty;
            return loaded;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        string temporaryPath = ConfigPath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, ConfigPath, overwrite: true);
    }

    private static string CleanSetting(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        string clean = new(value.Where(character => !char.IsControl(character)).ToArray());
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    public void Dispose()
    {
        // No persistent handles.
    }
}

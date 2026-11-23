using System.Text.Json;

namespace SokuNet;

internal sealed class AppSettings
{
    public bool PrivateMode { get; set; }
    public int AwayTimeoutMinutes { get; set; } = 5;
    public string DisplayName { get; set; } = "プレイヤー";
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

    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SokuNet",
        "config.json");

    public AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            if (!File.Exists(ConfigPath))
            {
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), JsonOptions);
            loaded ??= new AppSettings();
            loaded.AwayTimeoutMinutes = Math.Clamp(loaded.AwayTimeoutMinutes, 1, 120);
            loaded.DisplayName = CleanSetting(loaded.DisplayName, "プレイヤー", 24);
            loaded.RelayUrl ??= string.Empty;
            loaded.RoomCode ??= string.Empty;
            return loaded;
        }
        catch (JsonException)
        {
            var defaults = new AppSettings();
            try { Save(defaults); } catch { }
            return defaults;
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
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        string temporaryPath = ConfigPath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, ConfigPath, overwrite: true);
    }

    private static string CleanSetting(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        string clean = new(value.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    public void Dispose()
    {
        // No persistent handles.
    }
}

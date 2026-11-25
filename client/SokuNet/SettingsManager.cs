using System.Text.Json;
using System.Text.Json.Nodes;

namespace SokuNet;

internal sealed class AppSettings
{
    public const string DefaultRelayUrl = "https://sokunet-relay.sokunet.workers.dev";

    public bool PrivateMode { get; set; }
    public bool NotificationSoundEnabled { get; set; } = true;
    public int NotificationDurationSeconds { get; set; } = 3;
    public bool StartWithWindows { get; set; }
    public int ConsoleWindowWidth { get; set; }
    public int ConsoleWindowHeight { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    // Random per-installation ID. It is not a hardware identifier.
    public string InstallationId { get; set; } = Guid.NewGuid().ToString("N");
    public string RelayUrl { get; set; } = DefaultRelayUrl;
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
            if (!Guid.TryParseExact(loaded.InstallationId, "N", out _))
                loaded.InstallationId = Guid.NewGuid().ToString("N");
            EnsureInstallationIdSettingExists(loaded.InstallationId);
            loaded.NotificationDurationSeconds = Math.Clamp(loaded.NotificationDurationSeconds, 1, 30);
            EnsureNotificationDurationSettingExists(loaded.NotificationDurationSeconds);

            // Use the official relay by default for first launches and older/blank configs.
            // Explicit non-empty custom URLs are preserved.
            if (string.IsNullOrWhiteSpace(loaded.RelayUrl))
                loaded.RelayUrl = AppSettings.DefaultRelayUrl;
            else
                loaded.RelayUrl = loaded.RelayUrl.Trim().TrimEnd('/');
            EnsureDefaultRelayUrlSettingExists();

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

    private void EnsureNotificationDurationSettingExists(int defaultValue)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(ConfigPath));
            if (root is not JsonObject config)
                return;

            bool alreadyExists = config.Any(property =>
                string.Equals(property.Key, nameof(AppSettings.NotificationDurationSeconds), StringComparison.OrdinalIgnoreCase));
            if (alreadyExists)
                return;

            // Migrate older config.json files without discarding unrelated/custom keys.
            config[nameof(AppSettings.NotificationDurationSeconds)] = defaultValue;
            string temporaryPath = ConfigPath + ".tmp";
            File.WriteAllText(temporaryPath, config.ToJsonString(JsonOptions));
            File.Move(temporaryPath, ConfigPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The setting still uses its default even if an old config cannot be upgraded.
        }
    }


    private void EnsureDefaultRelayUrlSettingExists()
    {
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(ConfigPath));
            if (root is not JsonObject config)
                return;

            string? existingKey = config.Select(property => property.Key)
                .FirstOrDefault(key => string.Equals(key, nameof(AppSettings.RelayUrl), StringComparison.OrdinalIgnoreCase));

            if (existingKey is not null)
            {
                string? existingValue = null;
                try { existingValue = config[existingKey]?.GetValue<string>(); }
                catch (InvalidOperationException) { }

                if (!string.IsNullOrWhiteSpace(existingValue))
                    return;

                config[existingKey] = AppSettings.DefaultRelayUrl;
            }
            else
            {
                // Older configs may predate RelayUrl. Preserve all unrelated keys.
                config[nameof(AppSettings.RelayUrl)] = AppSettings.DefaultRelayUrl;
            }

            string temporaryPath = ConfigPath + ".tmp";
            File.WriteAllText(temporaryPath, config.ToJsonString(JsonOptions));
            File.Move(temporaryPath, ConfigPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The in-memory setting still uses the official relay if the config cannot be migrated.
        }
    }

    private void EnsureInstallationIdSettingExists(string installationId)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(ConfigPath));
            if (root is not JsonObject config)
                return;

            string? existingKey = config.Select(property => property.Key)
                .FirstOrDefault(key => string.Equals(key, nameof(AppSettings.InstallationId), StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null)
            {
                string? existingValue = null;
                try { existingValue = config[existingKey]?.GetValue<string>(); }
                catch (InvalidOperationException) { }
                if (string.Equals(existingValue, installationId, StringComparison.OrdinalIgnoreCase))
                    return;
                config[existingKey] = installationId;
            }
            else
            {
                // Add the ID to older configs without removing user-defined settings.
                config[nameof(AppSettings.InstallationId)] = installationId;
            }

            string temporaryPath = ConfigPath + ".tmp";
            File.WriteAllText(temporaryPath, config.ToJsonString(JsonOptions));
            File.Move(temporaryPath, ConfigPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Keep the in-memory ID even if the existing config cannot be upgraded.
        }
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

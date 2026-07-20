using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopTodo.Config;

public class AppConfig
{
    [JsonPropertyName("nextcloud_url")]
    public string NextcloudUrl { get; set; } = "https://your-server.example.com/remote.php/dav/";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("app_password")]
    public string AppPassword { get; set; } = "";

    [JsonPropertyName("calendar_name")]
    public string CalendarName { get; set; } = "待办事项";

    [JsonPropertyName("sync_interval_seconds")]
    public int SyncIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("card_width")]
    public int CardWidth { get; set; } = 380;

    [JsonPropertyName("card_height")]
    public int CardHeight { get; set; } = 500;

    [JsonPropertyName("card_position_x")]
    public int CardPositionX { get; set; } = 50;

    [JsonPropertyName("card_position_y")]
    public int CardPositionY { get; set; } = 50;

    [JsonPropertyName("snap_distance")]
    public int SnapDistance { get; set; } = 15;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "暗黑";

    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; }
}

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}

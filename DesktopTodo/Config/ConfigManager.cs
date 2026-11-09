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

    // 仅为兼容旧版明文配置：读取后立刻迁移到凭据管理器，保存时永不写回。
    [JsonPropertyName("app_password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAppPassword { get; set; }

    // 运行时凭据，永不序列化到磁盘。
    [JsonIgnore]
    public string AppPassword { get; set; } = "";

    [JsonPropertyName("calendar_name")]
    public string CalendarName { get; set; } = "待办事项";

    // 可选：自签证书 SHA-256 指纹。留空 = 使用系统默认严格校验。
    [JsonPropertyName("certificate_thumbprint")]
    public string CertificateThumbprint { get; set; } = "";

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

    /// <summary>深拷贝一份，供设置对话框「测试连接」使用，不污染当前配置。</summary>
    public AppConfig Clone() => (AppConfig)MemberwiseClone();
}

public static class ConfigManager
{
    private static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    /// <summary>配置读取异常（不阻断启动，由状态栏提示）。</summary>
    public static string? LastLoadError { get; private set; }

    public static AppConfig Load()
    {
        LastLoadError = null;
        AppConfig cfg;
        try
        {
            cfg = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig()
                : new AppConfig();
        }
        catch (Exception ex)
        {
            LastLoadError = "配置文件损坏，已使用默认配置：" + ex.Message;
            TryQuarantineBrokenConfig();
            cfg = new AppConfig();
        }

        Normalize(cfg);
        var target = CredentialStore.BuildTarget(cfg.NextcloudUrl, cfg.Username);

        // 迁移旧版明文密码：只有成功写入凭据管理器才从 JSON 中抹掉，避免密码丢失。
        if (!string.IsNullOrEmpty(cfg.LegacyAppPassword))
        {
            if (CredentialStore.Save(target, cfg.Username, cfg.LegacyAppPassword))
            {
                cfg.LegacyAppPassword = null;
                WriteJson(cfg);
            }
        }

        cfg.AppPassword = CredentialStore.Load(target) ?? "";
        return cfg;
    }

    public static void Save(AppConfig cfg)
    {
        Normalize(cfg);
        var target = CredentialStore.BuildTarget(cfg.NextcloudUrl, cfg.Username);
        if (!string.IsNullOrEmpty(cfg.AppPassword))
            CredentialStore.Save(target, cfg.Username, cfg.AppPassword);
        cfg.LegacyAppPassword = null;
        WriteJson(cfg);
    }

    public static void Normalize(AppConfig cfg)
    {
        if (cfg.SyncIntervalSeconds < 30 || cfg.SyncIntervalSeconds > 86400)
            cfg.SyncIntervalSeconds = 300;
        if (cfg.CardWidth < 260) cfg.CardWidth = 380;
        if (cfg.CardHeight < 200) cfg.CardHeight = 500;
        if (cfg.SnapDistance < 0 || cfg.SnapDistance > 100) cfg.SnapDistance = 15;
        cfg.CertificateThumbprint = (cfg.CertificateThumbprint ?? "")
            .Replace(":", "").Replace(" ", "").Trim();
    }

    /// <summary>连接前校验，返回错误说明；无错误返回 null。</summary>
    public static string? ValidateForConnection(AppConfig cfg)
    {
        if (!Uri.TryCreate(cfg.NextcloudUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Nextcloud URL 不是合法的 http/https 地址";
        if (string.IsNullOrWhiteSpace(cfg.Username)) return "用户名不能为空";
        if (string.IsNullOrWhiteSpace(cfg.AppPassword)) return "应用密码不能为空";
        if (uri.Scheme == Uri.UriSchemeHttp) return "警告：当前使用 http，凭据将以明文传输，建议改用 https";
        return null;
    }

    private static void WriteJson(AppConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ConfigPath + ".tmp";
        // 原子写入：避免崩溃/断电写坏配置。
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, true);
    }

    private static void TryQuarantineBrokenConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                File.Move(ConfigPath, ConfigPath + ".bad", true);
        }
        catch { /* 忽略：不因隔离失败阻断启动 */ }
    }
}

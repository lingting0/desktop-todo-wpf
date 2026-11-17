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

    /// <summary>手动代理地址（如 http://127.0.0.1:7897）。留空 = 走系统代理。</summary>
    [JsonPropertyName("proxy_url")]
    public string ProxyUrl { get; set; } = "";

    /// <summary>用户已确认允许 HTTP 明文传输凭据的来源（host:port）。</summary>
    [JsonPropertyName("allow_insecure_http")]
    public bool AllowInsecureHttp { get; set; }

    // ── 自动更新：全部是本机偏好，不进入仓库 ──

    /// <summary>是否允许后台检查更新；关掉后只能从托盘手动检查。</summary>
    [JsonPropertyName("update_check_enabled")]
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>上次检查时间（UTC Unix 秒），用于 24 小时节流。</summary>
    [JsonPropertyName("update_last_check_unix")]
    public long UpdateLastCheckUnix { get; set; }

    /// <summary>「稍后提醒」截止时间（UTC Unix 秒）。</summary>
    [JsonPropertyName("update_snooze_until_unix")]
    public long UpdateSnoozeUntilUnix { get; set; }

    /// <summary>「忽略此版本」记录的版本号。</summary>
    [JsonPropertyName("update_ignored_version")]
    public string UpdateIgnoredVersion { get; set; } = "";

    /// <summary>已经提示过的版本，避免每次启动重复写状态栏。</summary>
    [JsonPropertyName("update_notified_version")]
    public string UpdateNotifiedVersion { get; set; } = "";

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
            // 迁移失败时保留 LegacyAppPassword，下次启动再试。
        }

        cfg.AppPassword = CredentialStore.Load(target) ?? "";
        return cfg;
    }

    public static void Save(AppConfig cfg)
    {
        Normalize(cfg);
        var target = CredentialStore.BuildTarget(cfg.NextcloudUrl, cfg.Username);
        if (!string.IsNullOrEmpty(cfg.AppPassword))
        {
            if (!CredentialStore.Save(target, cfg.Username, cfg.AppPassword))
                throw new IOException("密码未能保存到 Windows 凭据管理器，请重试");
        }
        // 只有凭据写入成功（或本来就没有密码）才能清除旧字段。
        cfg.LegacyAppPassword = null;
        WriteJson(cfg);
    }

    /// <summary>
    /// 只持久化更新偏好，不触碰凭据管理器。
    /// 如果旧密码迁移尚未完成（LegacyAppPassword 非空），不覆盖配置，
    /// 避免把唯一密码副本写丢。
    /// </summary>
    public static void SaveUpdateState(AppConfig cfg)
    {
        try
        {
            // 迁移未完成时不覆盖配置文件，防止丢失唯一的旧密码副本。
            if (!string.IsNullOrEmpty(cfg.LegacyAppPassword))
                return;
            WriteJson(cfg);
        }
        catch { /* 更新状态写盘失败不影响更新检测 */ }
    }

    public static void Normalize(AppConfig cfg)
    {
        cfg.NextcloudUrl = (cfg.NextcloudUrl ?? "").Trim();
        cfg.Username = (cfg.Username ?? "").Trim();
        cfg.CalendarName ??= "";
        cfg.Theme = string.IsNullOrWhiteSpace(cfg.Theme) ? "暗黑" : cfg.Theme.Trim();
        cfg.CertificateThumbprint = (cfg.CertificateThumbprint ?? "")
            .Replace(":", "").Replace(" ", "").Trim();
        cfg.ProxyUrl = (cfg.ProxyUrl ?? "").Trim();
        cfg.UpdateIgnoredVersion ??= "";
        cfg.UpdateNotifiedVersion ??= "";

        if (cfg.SyncIntervalSeconds < 30 || cfg.SyncIntervalSeconds > 86400)
            cfg.SyncIntervalSeconds = 300;
        cfg.CardWidth = Math.Clamp(cfg.CardWidth, 260, 1600);
        cfg.CardHeight = Math.Clamp(cfg.CardHeight, 200, 1600);
        if (cfg.SnapDistance < 0 || cfg.SnapDistance > 100) cfg.SnapDistance = 15;
        if (cfg.UpdateLastCheckUnix < 0) cfg.UpdateLastCheckUnix = 0;
        if (cfg.UpdateSnoozeUntilUnix < 0) cfg.UpdateSnoozeUntilUnix = 0;
    }

    /// <summary>连接前校验，返回错误说明；无错误返回 null；有警告返回 "警告:..."。</summary>
    public static string? ValidateForConnection(AppConfig cfg)
    {
        if (!Uri.TryCreate(cfg.NextcloudUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Nextcloud URL 不是合法的 http/https 地址";
        if (string.IsNullOrWhiteSpace(cfg.Username)) return "用户名不能为空";
        if (string.IsNullOrWhiteSpace(cfg.AppPassword)) return "应用密码不能为空";
        if (uri.Scheme == Uri.UriSchemeHttp && !cfg.AllowInsecureHttp)
            return "警告：当前使用 http，凭据将以明文传输，建议改用 https。如确需使用 http，请先点击确认。";
        return null;
    }

    private static readonly object WriteLock = new();

    private static void WriteJson(AppConfig cfg)
    {
        lock (WriteLock)
        {
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            // 使用唯一临时文件名，避免多实例冲突。
            var tmp = ConfigPath + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, true);
        }
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

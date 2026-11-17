using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTodo.Services;

/// <summary>GitHub Release 上发现的一个可用新版本。</summary>
public sealed class UpdateInfo
{
    public string Tag { get; set; } = "";
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string? ChecksumUrl { get; set; }
    public long Size { get; set; }
    public string HtmlUrl { get; set; } = "";
}

/// <summary>
/// 检查 / 下载 / 校验更新包。只读取本仓库的公开 Release，不接触任何用户数据。
/// 网络失败由调用方决定如何提示（自动检查时完全静默）。
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "lingting0";
    private const string Repo = "desktop-todo-wpf";
    private const string AssetSuffix = "-win-x64.zip";

    private static readonly string ApiUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly string[] RequiredFiles =
    {
        "DesktopTodo.exe", "DesktopTodo.dll", "DesktopTodo.runtimeconfig.json"
    };

    private readonly HttpClient _http;

    /// <summary>
    /// 仅用于验收演练：命令行 --pretend-version 可指定一个较低的「当前版本」，
    /// 以便真实走一遍「提醒 → 一键更新」流程。正常启动永远为 null。
    /// </summary>
    public static Version? OverrideVersion { get; set; }

    /// <summary>程序集版本（csproj 的 Version）；用于与 Release 的 tag 比较。</summary>
    public static Version CurrentVersion =>
        OverrideVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText => Normalize(CurrentVersion).ToString(3);

    public static string UpdatesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopTodo", "updates");

    public UpdateService()
    {
        var handler = new HttpClientHandler
        {
            // 走系统代理设置；检测失败一律静默，不影响主功能。
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub REST API 要求带 User-Agent，否则返回 403。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopTodo/" + CurrentVersionText);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// 返回可更新的版本信息；已是最新 / 无可用资产返回 null；网络或解析异常抛出。
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = GetString(root, "tag_name");
        var remote = ParseTag(tag);
        if (remote == null || remote <= Normalize(CurrentVersion))
            return null; // 已是最新，或 tag 不规范

        string? zipUrl = null, checksumUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = GetString(a, "name");
                var url = GetString(a, "browser_download_url");
                if (!IsAllowedUrl(url)) continue;
                if (name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = url;
                    size = GetLong(a, "size");
                }
                else if (name.EndsWith(AssetSuffix + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        // 没有可安装的资产时不打扰用户。
        if (zipUrl == null) return null;

        return new UpdateInfo
        {
            Tag = tag,
            Version = remote.ToString(3),
            Notes = TrimNotes(GetString(root, "body")),
            DownloadUrl = zipUrl,
            ChecksumUrl = checksumUrl,
            Size = size,
            HtmlUrl = GetString(root, "html_url")
        };
    }

    /// <summary>下载、校验并解压到暂存目录，返回可直接复制的目录路径。</summary>
    public async Task<string> PrepareAsync(UpdateInfo info, IProgress<string> progress,
                                           CancellationToken ct = default)
    {
        // 安全护栏：无论「当前版本」是否被演练参数伪装，都不允许降级到更旧的程序。
        var real = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));
        var target = ParseTag(info.Tag);
        if (target != null && target < real)
            throw new InvalidOperationException(
                $"拒绝降级：更新包 v{target.ToString(3)} 低于当前程序 v{real.ToString(3)}");

        var dir = Path.Combine(UpdatesRoot, Sanitize(info.Tag));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* 旧暂存目录清不掉就复用，不阻断 */ }
        Directory.CreateDirectory(dir);

        var zipPath = Path.Combine(dir, "package.zip");
        await DownloadAsync(info.DownloadUrl, zipPath, info.Size, progress, ct);

        progress.Report("正在校验更新包...");
        if (!string.IsNullOrEmpty(info.ChecksumUrl))
            await VerifyChecksumAsync(info.ChecksumUrl!, zipPath, ct);

        var staged = Path.Combine(dir, "staged");
        Directory.CreateDirectory(staged);
        ExtractSafe(zipPath, staged);

        foreach (var f in RequiredFiles)
            if (!File.Exists(Path.Combine(staged, f)))
                throw new InvalidDataException("更新包缺少必需文件：" + f);

        // 永不把发行包里的配置覆盖到用户目录（本机 config.json 保留真实值）。
        var stray = Path.Combine(staged, "config.json");
        if (File.Exists(stray)) File.Delete(stray);

        return staged;
    }

    /// <summary>
    /// 从「暂存目录里的新 exe」启动更新器。这样更新器占用的是 staged 的文件，
    /// 目标目录中的 DesktopTodo.exe 才能被正常覆盖。
    /// </summary>
    public static bool StartUpdater(string stagedDir, int pid, bool noLaunch = false)
    {
        var exe = Path.Combine(stagedDir, "DesktopTodo.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到更新器", exe);

        var dest = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = stagedDir
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(stagedDir);
        psi.ArgumentList.Add(dest);
        psi.ArgumentList.Add(pid.ToString());
        if (noLaunch) psi.ArgumentList.Add("--no-launch");
        return Process.Start(psi) != null;
    }

    // ── 内部实现 ──

    private async Task DownloadAsync(string url, string path, long expectedSize,
                                     IProgress<string> progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = expectedSize > 0
            ? expectedSize
            : resp.Content.Headers.ContentLength ?? 0;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                            81920, useAsync: true);
        var buffer = new byte[81920];
        long read = 0;
        int lastPct = -1;
        int n;
        while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                var pct = (int)(read * 100 / total);
                if (pct != lastPct && (pct % 5 == 0 || pct == 100))
                {
                    lastPct = pct;
                    progress.Report($"正在下载更新包... {pct}%");
                }
            }
        }
        if (total > 0 && read != total)
            throw new IOException($"下载不完整：期望 {total} 字节，实际 {read} 字节");
        progress.Report("下载完成");
    }

    private async Task VerifyChecksumAsync(string url, string zipPath, CancellationToken ct)
    {
        var text = await _http.GetStringAsync(url, ct);
        var hash = text.Split(new[] { ' ', '\t', '\r', '\n' },
                              StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(hash) || hash.Length != 64 ||
            !hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            throw new InvalidDataException("校验文件格式不正确，已取消更新");

        await using var fs = File.OpenRead(zipPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!string.Equals(hash, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包校验失败（SHA-256 不匹配），已取消更新");
    }

    private static void ExtractSafe(string zipPath, string staged)
    {
        var root = Path.GetFullPath(staged) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            // 防 Zip Slip：拒绝绝对路径与 .. 跳跃。
            if (name.Length == 0 || name.Contains("..") || Path.IsPathRooted(name)) continue;
            var target = Path.GetFullPath(Path.Combine(staged, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (Path.GetFileName(target).Equals("config.json", StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static Version? ParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash]; // 去掉 -beta / -rc 之类后缀
        var parts = s.Split('.');
        if (parts.Length < 2 || parts.Length > 4) return null;
        if (!int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min))
            return null;
        int build = 0;
        if (parts.Length >= 3) int.TryParse(parts[2], out build);
        return new Version(maj, min, Math.Max(0, build));
    }

    private static string Sanitize(string tag)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(tag.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }

    private static string TrimNotes(string body)
    {
        body = body.Replace("\r\n", "\n").Trim();
        return body.Length > 1200 ? body[..1200] + "\n..." : body;
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : 0;
}

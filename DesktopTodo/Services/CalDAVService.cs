using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using DesktopTodo.Models;

namespace DesktopTodo.Services;

public class CalDAVService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _requestedCalendar;
    private string? _calendarUrl;

    public string CalendarName { get; private set; } = "?";

    public CalDAVService(string url, string username, string password,
                         string certificateThumbprint = "", string calendarName = "")
    {
        _baseUrl = (url ?? "").TrimEnd('/');
        _requestedCalendar = calendarName ?? "";

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
        };

        // P0-1：默认走系统严格证书校验。只有显式登记了指纹时才放宽，
        // 且仅接受该指纹对应的证书（可用于自签证书），不再无条件信任所有证书。
        var thumb = (certificateThumbprint ?? "").Replace(":", "").Replace(" ", "").Trim();
        if (thumb.Length > 0)
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (cert == null) return false;
                try
                {
                    return string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256),
                                         thumb, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            };
        }

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", "DesktopTodo-WPF/1.1");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public void Dispose() => _http.Dispose();

    public async Task ConnectAsync()
    {
        var principalUrl = await FindPrincipalAsync();
        var calHome = await FindCalendarHomeAsync(principalUrl);
        var calendars = await ListCalendarsAsync(calHome);
        await PickCalendarAsync(calendars);
    }

    /// <summary>供设置页「测试连接」使用：不写盘、不改当前服务实例。</summary>
    public async Task<(bool ok, string message)> TestConnectionAsync()
    {
        try
        {
            await ConnectAsync();
            var tasks = await FetchAllAsync();
            return (true, $"连接成功：日历「{CalendarName}」，共 {tasks.Count} 个任务");
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
    }

    public async Task<List<TaskData>> FetchAllAsync()
    {
        if (_calendarUrl == null) throw new InvalidOperationException("尚未连接日历");
        return await FetchTasksFromAsync(_calendarUrl);
    }

    public async Task AddAsync(TaskData task)
    {
        if (_calendarUrl == null) throw new InvalidOperationException("尚未连接日历");
        var ical = BuildICal(task);
        var req = new HttpRequestMessage(HttpMethod.Put, ResourceUrl(task))
        {
            Content = CalendarContent(ical)
        };
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new InvalidOperationException("服务器上已存在相同 UID 的任务，未覆盖");
        EnsureOk(resp, "新增任务");
        UpdateEtag(resp, task);
        task.RawICal = ical;
        task.CreatedAt ??= DateTime.Now;
    }

    public async Task UpdateAsync(TaskData task)
    {
        if (_calendarUrl == null) throw new InvalidOperationException("尚未连接日历");
        var ical = BuildICal(task);
        var req = new HttpRequestMessage(HttpMethod.Put, ResourceUrl(task))
        {
            Content = CalendarContent(ical)
        };
        if (!string.IsNullOrWhiteSpace(task.Etag))
            req.Headers.TryAddWithoutValidation("If-Match", task.Etag);
        var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new InvalidOperationException("该任务已被其他设备修改，请刷新后重试");
        EnsureOk(resp, "更新任务");
        UpdateEtag(resp, task);
        task.RawICal = ical;
    }

    public async Task DeleteAsync(TaskData task)
    {
        if (_calendarUrl == null) throw new InvalidOperationException("尚未连接日历");
        var req = new HttpRequestMessage(HttpMethod.Delete, ResourceUrl(task));
        if (!string.IsNullOrWhiteSpace(task.Etag))
            req.Headers.TryAddWithoutValidation("If-Match", task.Etag);
        var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // 已删除视为成功
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new InvalidOperationException("该任务已被其他设备修改，未删除");
        EnsureOk(resp, "删除任务"); // P0-2：删除失败不再静默
    }

    // ── CalDAV protocol ──

    private static StringContent CalendarContent(string ical)
        => new StringContent(ical, Encoding.UTF8, "text/calendar");

    private static void EnsureOk(HttpResponseMessage resp, string action)
    {
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{action}失败：服务器返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    private static void UpdateEtag(HttpResponseMessage resp, TaskData task)
    {
        var etag = resp.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(etag)) task.Etag = etag;
    }

    private string ResourceUrl(TaskData task)
    {
        if (!string.IsNullOrWhiteSpace(task.Href)) return FullUrl(task.Href);
        return (_calendarUrl ?? _baseUrl).TrimEnd('/') + "/" + Uri.EscapeDataString(task.Uid) + ".ics";
    }

    private string FullUrl(string href)
    {
        href = (href ?? "").Trim();
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        var idx = _baseUrl.IndexOf("/remote.php", StringComparison.OrdinalIgnoreCase);
        var origin = idx >= 0 ? _baseUrl[..idx] : _baseUrl;
        if (!href.StartsWith("/")) href = "/" + href;
        return origin.TrimEnd('/') + href;
    }

    private async Task<HttpResponseMessage> PropfindAsync(string url, string body, int depth = 0)
    {
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        req.Headers.Add("Depth", depth.ToString());
        return await _http.SendAsync(req);
    }

    private async Task<string> FindPrincipalAsync()
    {
        var body = @"<?xml version=""1.0""?>
<D:propfind xmlns:D=""DAV:""><D:prop><D:current-user-principal/></D:prop></D:propfind>";
        var resp = await PropfindAsync(_baseUrl, body, 0);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new Exception("认证失败 — 检查用户名和应用密码");
        EnsureOk(resp, "读取 principal");

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var href = xml.Descendants(XName.Get("href", "DAV:"))
            .Select(e => e.Value.Trim())
            .FirstOrDefault(h => h.Contains("/principals/", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(href))
            throw new Exception("服务器未返回 current-user-principal，请检查 Nextcloud URL");
        return FullUrl(href);
    }

    private async Task<string> FindCalendarHomeAsync(string principalUrl)
    {
        var body = @"<?xml version=""1.0""?>
<D:propfind xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav""><D:prop><C:calendar-home-set/></D:prop></D:propfind>";
        var resp = await PropfindAsync(principalUrl, body, 0);
        EnsureOk(resp, "读取 calendar-home-set");

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var href = xml.Descendants(XName.Get("calendar-home-set", "urn:ietf:params:xml:ns:caldav"))
            .Descendants(XName.Get("href", "DAV:"))
            .Select(e => e.Value.Trim())
            .FirstOrDefault();
        if (string.IsNullOrEmpty(href))
            throw new Exception("服务器未返回 calendar-home-set");
        return FullUrl(href);
    }

    private async Task<List<(string url, string name)>> ListCalendarsAsync(string calHome)
    {
        var body = @"<?xml version=""1.0""?>
<D:propfind xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
<D:prop><D:displayname/><D:resourcetype/><C:supported-calendar-component-set/></D:prop></D:propfind>";
        var resp = await PropfindAsync(calHome, body, 1);
        EnsureOk(resp, "列出日历");

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var nsD = XNamespace.Get("DAV:");
        var nsC = XNamespace.Get("urn:ietf:params:xml:ns:caldav");
        var results = new List<(string url, string name)>();

        foreach (var response in xml.Descendants(nsD + "response"))
        {
            var href = response.Element(nsD + "href")?.Value.Trim() ?? "";
            if (string.IsNullOrEmpty(href)) continue;
            var hasVtodo = response.Descendants(nsC + "comp")
                .Any(c => (string?)c.Attribute("name") == "VTODO");
            if (!hasVtodo) continue;
            var name = response.Descendants(nsD + "displayname")
                .Select(e => e.Value.Trim()).FirstOrDefault() ?? "?";
            results.Add((FullUrl(href), name));
        }
        return results;
    }

    private async Task PickCalendarAsync(List<(string url, string name)> calendars)
    {
        if (calendars.Count == 0)
            throw new Exception("未找到支持 VTODO 的日历，请在 Nextcloud 中启用 Tasks 应用");

        // 优先使用配置里指定的日历名，不再盲目选「任务最多」的那个。
        if (!string.IsNullOrWhiteSpace(_requestedCalendar))
        {
            foreach (var cal in calendars)
            {
                if (string.Equals(cal.name, _requestedCalendar, StringComparison.OrdinalIgnoreCase))
                {
                    _calendarUrl = cal.url;
                    CalendarName = cal.name;
                    return;
                }
            }
        }

        var best = calendars[0];
        var bestCount = -1;
        foreach (var cal in calendars)
        {
            int count;
            try { count = (await FetchTasksFromAsync(cal.url)).Count; }
            catch { count = 0; }
            if (count > bestCount) { best = cal; bestCount = count; }
        }
        _calendarUrl = best.url;
        CalendarName = best.name;
    }

    private async Task<List<TaskData>> FetchTasksFromAsync(string calUrl)
    {
        var body = @"<?xml version=""1.0""?>
<C:calendar-query xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
<D:prop><D:getetag/><C:calendar-data/></D:prop>
<C:filter><C:comp-filter name=""VCALENDAR""><C:comp-filter name=""VTODO""/></C:comp-filter></C:filter>
</C:calendar-query>";
        var req = new HttpRequestMessage(new HttpMethod("REPORT"), calUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        req.Headers.Add("Depth", "1");
        var resp = await _http.SendAsync(req);
        // 不再把 REPORT 的 body 拿去发 PROPFIND，也不吞掉网络异常。
        EnsureOk(resp, "查询任务");

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var nsD = XNamespace.Get("DAV:");
        var nsC = XNamespace.Get("urn:ietf:params:xml:ns:caldav");
        var tasks = new List<TaskData>();

        foreach (var response in xml.Descendants(nsD + "response"))
        {
            var href = response.Element(nsD + "href")?.Value.Trim() ?? "";
            var etag = response.Descendants(nsD + "getetag")
                .Select(e => e.Value.Trim()).FirstOrDefault();
            foreach (var data in response.Descendants(nsC + "calendar-data"))
            {
                var ical = data.Value;
                var task = ParseVTodo(ical);
                if (task == null) continue;
                task.Href = href;
                task.Etag = etag;
                task.RawICal = ical;
                tasks.Add(task);
            }
        }
        return tasks;
    }

    // ── iCalendar（round-trip 保留未知字段）──

    private static readonly string[] ManagedProps =
    {
        "UID", "SUMMARY", "STATUS", "PRIORITY", "DTSTAMP", "LAST-MODIFIED",
        "CREATED", "COMPLETED", "PERCENT-COMPLETE", "DUE",
    };

    private static string BuildICal(TaskData t)
    {
        var source = string.IsNullOrEmpty(t.RawICal)
            ? UnfoldLines("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//DesktopTodo//WPF//EN\r\nBEGIN:VTODO\r\nEND:VTODO\r\nEND:VCALENDAR")
            : UnfoldLines(t.RawICal);

        int start = source.FindIndex(l => PropertyName(l) == "BEGIN" &&
                                          PropertyValue(l).Equals("VTODO", StringComparison.OrdinalIgnoreCase));
        int end = source.FindLastIndex(l => PropertyName(l) == "END" &&
                                            PropertyValue(l).Equals("VTODO", StringComparison.OrdinalIgnoreCase));

        var lines = new List<string>();
        if (start < 0 || end <= start)
        {
            lines.Add("BEGIN:VCALENDAR");
            lines.Add("VERSION:2.0");
            lines.Add("PRODID:-//DesktopTodo//WPF//EN");
            lines.Add("BEGIN:VTODO");
            lines.AddRange(BuildManagedLines(t));
            lines.Add("END:VTODO");
            lines.Add("END:VCALENDAR");
        }
        else
        {
            for (int i = 0; i <= start; i++) lines.Add(source[i]);
            for (int i = start + 1; i < end; i++)
            {
                if (ManagedProps.Contains(PropertyName(source[i]))) continue;
                lines.Add(source[i]);
            }
            lines.AddRange(BuildManagedLines(t));
            lines.Add(source[end]);
            for (int i = end + 1; i < source.Count; i++) lines.Add(source[i]);
        }
        return string.Join("\r\n", FoldAll(lines)) + "\r\n";
    }

    private static List<string> BuildManagedLines(TaskData t)
    {
        var now = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "UID:" + t.Uid,
            "DTSTAMP:" + now,
            "LAST-MODIFIED:" + now,
            "SUMMARY:" + Escape(t.Summary ?? ""),
            "STATUS:" + (t.Completed ? "COMPLETED" : "NEEDS-ACTION"),
            "PERCENT-COMPLETE:" + (t.Completed ? "100" : "0"),
        };
        if (t.Priority > 0) lines.Add("PRIORITY:" + Math.Clamp(t.Priority, 1, 9));
        if (t.CreatedAt.HasValue) lines.Add("CREATED:" + FormatIcalDate(t.CreatedAt.Value));
        if (t.CompletedAt.HasValue) lines.Add("COMPLETED:" + FormatIcalDate(t.CompletedAt.Value));
        if (t.DueDate.HasValue) lines.Add("DUE:" + FormatIcalDate(t.DueDate.Value));
        return lines;
    }

    private static TaskData? ParseVTodo(string ical)
    {
        if (string.IsNullOrWhiteSpace(ical)) return null;
        var lines = UnfoldLines(ical);
        int start = lines.FindIndex(l => PropertyName(l) == "BEGIN" &&
                                          PropertyValue(l).Equals("VTODO", StringComparison.OrdinalIgnoreCase));
        int end = lines.FindLastIndex(l => PropertyName(l) == "END" &&
                                            PropertyValue(l).Equals("VTODO", StringComparison.OrdinalIgnoreCase));
        if (start < 0 || end <= start) return null;

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start + 1; i < end; i++)
        {
            var name = PropertyName(lines[i]);
            if (name.Length == 0) continue;
            props[name] = Unescape(PropertyValue(lines[i]));
        }

        var status = props.GetValueOrDefault("STATUS") ?? "";
        var percent = props.GetValueOrDefault("PERCENT-COMPLETE") ?? "0";
        bool completed = status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) || percent == "100";

        return new TaskData
        {
            Uid = props.GetValueOrDefault("UID") ?? Guid.NewGuid().ToString(),
            Summary = props.GetValueOrDefault("SUMMARY") ?? "",
            Completed = completed,
            Priority = int.TryParse(props.GetValueOrDefault("PRIORITY") ?? "0", out var p) ? p : 0,
            CreatedAt = ParseIcalDate(props.GetValueOrDefault("CREATED")),
            CompletedAt = ParseIcalDate(props.GetValueOrDefault("COMPLETED")),
            DueDate = ParseIcalDate(props.GetValueOrDefault("DUE")),
        };
    }

    private static string PropertyName(string line)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return "";
        var head = line[..idx];
        var semi = head.IndexOf(';');
        if (semi >= 0) head = head[..semi];
        return head.Trim().ToUpperInvariant();
    }

    private static string PropertyValue(string line)
    {
        var idx = line.IndexOf(':');
        return idx < 0 ? "" : line[(idx + 1)..].Trim();
    }

    private static List<string> UnfoldLines(string ical)
    {
        var normalized = ical.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length == 0) continue;
            if ((line[0] == ' ' || line[0] == '\t') && lines.Count > 0)
                lines[lines.Count - 1] += line[1..];
            else
                lines.Add(line);
        }
        return lines;
    }

    private static List<string> FoldAll(List<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines) result.AddRange(FoldLine(line));
        return result;
    }

    private static IEnumerable<string> FoldLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length == 0) { yield return ""; yield break; }
        int pos = 0;
        bool first = true;
        while (pos < bytes.Length)
        {
            int budget = first ? 75 : 74; // 续行首字符为空格，占 1 字节
            int take = Math.Min(budget, bytes.Length - pos);
            // 不切断 UTF-8 多字节字符
            while (take > 0 && pos + take < bytes.Length && (bytes[pos + take] & 0xC0) == 0x80) take--;
            if (take == 0) take = Math.Min(budget, bytes.Length - pos);
            yield return (first ? "" : " ") + Encoding.UTF8.GetString(bytes, pos, take);
            first = false;
            pos += take;
        }
    }

    private static string Escape(string? s)
    {
        var sb = new StringBuilder();
        foreach (var ch in (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case ';': sb.Append("\\;"); break;
                case ',': sb.Append("\\,"); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[++i];
                switch (next)
                {
                    case 'n': case 'N': sb.Append('\n'); break;
                    case '\\': sb.Append('\\'); break;
                    case ';': sb.Append(';'); break;
                    case ',': sb.Append(','); break;
                    default: sb.Append('\\').Append(next); break;
                }
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string FormatIcalDate(DateTime value)
        => value.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);

    private static DateTime? ParseIcalDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim().TrimEnd('Z');
        if (DateTime.TryParseExact(s, new[] { "yyyyMMddTHHmmss", "yyyyMMdd" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt.ToLocalTime();
        return null;
    }
}

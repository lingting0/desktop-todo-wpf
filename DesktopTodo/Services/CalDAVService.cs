using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using DesktopTodo.Models;

namespace DesktopTodo.Services;

public class CalDAVService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private string? _calendarUrl;

    public string CalendarName { get; private set; } = "?";

    public CalDAVService(string url, string username, string password)
    {
        _baseUrl = url.TrimEnd('/');
        _http = new HttpClient(new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true // self-signed / Let's Encrypt
        });
        _http.DefaultRequestHeaders.Add("User-Agent", "DesktopTodo-WPF/1.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task ConnectAsync()
    {
        var principalUrl = await FindPrincipalAsync();
        var calHome = await FindCalendarHomeAsync(principalUrl);
        var calendars = await ListCalendarsAsync(calHome);
        await PickBestCalendarAsync(calendars);
    }

    public async Task<List<TaskData>> FetchAllAsync()
    {
        if (_calendarUrl == null) throw new InvalidOperationException("Not connected");
        return await FetchTasksFromAsync(_calendarUrl);
    }

    public async Task AddAsync(TaskData task)
    {
        if (_calendarUrl == null) throw new InvalidOperationException("Not connected");
        var ical = BuildICal(task);
        var url = $"{_calendarUrl}{task.Uid}.ics";
        var resp = await _http.PutAsync(url, new StringContent(ical, Encoding.UTF8, "text/calendar"));
        resp.EnsureSuccessStatusCode();
        task.CreatedAt ??= DateTime.Now;
    }

    public async Task UpdateAsync(TaskData task)
    {
        if (_calendarUrl == null) throw new InvalidOperationException("Not connected");
        var ical = BuildICal(task);
        var url = $"{_calendarUrl}{task.Uid}.ics";
        var resp = await _http.PutAsync(url, new StringContent(ical, Encoding.UTF8, "text/calendar"));
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string uid)
    {
        if (_calendarUrl == null) throw new InvalidOperationException("Not connected");
        var url = $"{_calendarUrl}{uid}.ics";
        await _http.DeleteAsync(url);
    }

    // ── CalDAV protocol ──

    private string FullUrl(string href)
    {
        href = href.Trim();
        if (href.StartsWith("http")) return href;
        var origin = _baseUrl.Contains("/remote.php")
            ? _baseUrl[.._baseUrl.IndexOf("/remote.php")]
            : _baseUrl;
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

    private async Task<HttpResponseMessage> ReportAsync(string url, string body)
    {
        var req = new HttpRequestMessage(new HttpMethod("REPORT"), url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        req.Headers.Add("Depth", "1");
        return await _http.SendAsync(req);
    }

    private async Task<string> FindPrincipalAsync()
    {
        var body = @"<?xml version=""1.0""?>
<D:propfind xmlns:D=""DAV:""><D:prop><D:current-user-principal/></D:prop></D:propfind>";
        var resp = await PropfindAsync(_baseUrl, body, 0);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new Exception("认证失败 — 检查用户名和应用密码");
        resp.EnsureSuccessStatusCode();

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var hrefs = xml.Descendants(XName.Get("href", "DAV:"))
            .Select(e => e.Value.Trim())
            .Where(h => h.Contains("/principals/"));
        return FullUrl(hrefs.First());
    }

    private async Task<string> FindCalendarHomeAsync(string principalUrl)
    {
        var body = @"<?xml version=""1.0""?>
<D:propfind xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav""><D:prop><C:calendar-home-set/></D:prop></D:propfind>";
        var resp = await PropfindAsync(principalUrl, body, 0);
        resp.EnsureSuccessStatusCode();

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var href = xml.Descendants(XName.Get("calendar-home-set", "urn:ietf:params:xml:ns:caldav"))
            .Descendants(XName.Get("href", "DAV:"))
            .Select(e => e.Value.Trim())
            .First();
        return FullUrl(href);
    }

    private async Task<List<(string url, string name)>> ListCalendarsAsync(string calHome)
    {
        var body = @"<?xml version=""1.0""?>
<D:propfind xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
<D:prop><D:displayname/><D:resourcetype/><C:supported-calendar-component-set/></D:prop></D:propfind>";
        var resp = await PropfindAsync(calHome, body, 1);
        resp.EnsureSuccessStatusCode();

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var results = new List<(string url, string name)>();
        var nsD = XNamespace.Get("DAV:");
        var nsC = XNamespace.Get("urn:ietf:params:xml:ns:caldav");

        foreach (var respEl in xml.Descendants(nsD + "response"))
        {
            var href = respEl.Element(nsD + "href")?.Value.Trim() ?? "";
            if (string.IsNullOrEmpty(href)) continue;

            var hasVtodo = respEl.Descendants(nsC + "comp")
                .Any(c => (string?)c.Attribute("name") == "VTODO");
            if (!hasVtodo) continue;

            var name = respEl.Descendants(nsD + "displayname")
                .Select(e => e.Value.Trim())
                .FirstOrDefault() ?? "?";

            results.Add((FullUrl(href), name));
        }
        return results;
    }

    private async Task PickBestCalendarAsync(List<(string url, string name)> calendars)
    {
        if (calendars.Count == 0)
            throw new Exception("No VTODO calendar found. 在 Nextcloud 中启用 Tasks 应用。");

        var best = calendars[0];
        var bestCount = 0;

        foreach (var cal in calendars)
        {
            var tasks = await FetchTasksFromAsync(cal.url);
            if (tasks.Count > bestCount)
            {
                best = cal;
                bestCount = tasks.Count;
            }
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

        HttpResponseMessage resp;
        try { resp = await ReportAsync(calUrl, body); }
        catch { resp = await PropfindAsync(calUrl, body, 1); }

        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var nsC = XNamespace.Get("urn:ietf:params:xml:ns:caldav");
        var tasks = new List<TaskData>();

        foreach (var cd in xml.Descendants(nsC + "calendar-data"))
        {
            var ical = cd.Value;
            var task = ParseVTodo(ical);
            if (task != null) tasks.Add(task);
        }
        return tasks;
    }

    // ── iCalendar ──

    private static string BuildICal(TaskData t)
    {
        var now = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var status = t.Completed ? "COMPLETED" : "NEEDS-ACTION";
        var prio = t.Priority > 0 ? $"\nPRIORITY:{t.Priority}" : "";
        return "BEGIN:VCALENDAR\r\n" +
               "VERSION:2.0\r\n" +
               "PRODID:-//DesktopTodo//WPF//EN\r\n" +
               "BEGIN:VTODO\r\n" +
               $"UID:{t.Uid}\r\n" +
               $"DTSTAMP:{now}\r\n" +
               $"SUMMARY:{Escape(t.Summary)}\r\n" +
               $"STATUS:{status}{prio}\r\n" +
               "END:VTODO\r\n" +
               "END:VCALENDAR";
    }

    private static TaskData? ParseVTodo(string ical)
    {
        if (string.IsNullOrEmpty(ical) || !ical.Contains("BEGIN:VTODO")) return null;
        var match = Regex.Match(ical, @"BEGIN:VTODO\r?\n(.*?)END:VTODO", RegexOptions.Singleline);
        if (!match.Success) return null;

        var props = new Dictionary<string, string>();
        foreach (var line in match.Groups[1].Value.Split('\n'))
        {
            var trimmed = line.Trim();
            var idx = trimmed.IndexOf(':');
            if (idx < 0) continue;
            var key = trimmed[..idx].Trim().ToUpper();
            var val = trimmed[(idx + 1)..].Trim();
            props[key] = Unescape(val);
        }

        return new TaskData
        {
            Uid = props.GetValueOrDefault("UID", Guid.NewGuid().ToString()),
            Summary = props.GetValueOrDefault("SUMMARY", ""),
            Completed = props.GetValueOrDefault("STATUS", "").Equals("COMPLETED", StringComparison.OrdinalIgnoreCase),
            Priority = int.TryParse(props.GetValueOrDefault("PRIORITY", "0"), out var p) ? p : 0,
        };
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

    private static string Unescape(string s)
        => s.Replace("\\n", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
}

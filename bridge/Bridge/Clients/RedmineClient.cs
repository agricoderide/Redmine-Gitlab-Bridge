using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bridge.Contracts;
using Bridge.Data;
using Bridge.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bridge.Services;


public sealed class RedmineClient
{
    public record RmMember(int Id, string Name);

    private readonly HttpClient _http;
    public readonly RedmineOptions _opt;
    public readonly TrackersKeys _trackers;
    private readonly IServiceScopeFactory _scopeFactory;

    public RedmineClient(HttpClient http, IServiceScopeFactory scopeFactory, IOptions<RedmineOptions> opt, IOptions<TrackersKeys> trackers)
    {
        _http = http;
        _opt = opt.Value;
        _scopeFactory = scopeFactory;

        _trackers = trackers.Value;
        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            _http.DefaultRequestHeaders.Add("X-Redmine-API-Key", _opt.ApiKey);
    }

    // Connectivity check
    public async Task<(bool ok, string message)> PingAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("projects.json?limit=1", ct);
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var count = doc.RootElement.TryGetProperty("projects", out var arr)
            ? arr.GetArrayLength()
            : 0;
        return (true, $"OK. projects={count}");
    }

    // Get all redmine projects that have a gitlab url
    public async Task<IReadOnlyList<ProjectLink>> GetProjectsWithGitLabLinksAsync(
        string customFieldName,
        CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("projects.json?include=custom_fields", ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("projects", out var arr))
            return Array.Empty<ProjectLink>();

        var list = new List<ProjectLink>(arr.GetArrayLength());
        foreach (var p in arr.EnumerateArray())
        {
            var id = GetInt(p, "id");
            var identifier = TryGetString(p, "identifier") ?? id.ToString();
            var name = TryGetString(p, "name") ?? identifier;

            var url = ExtractGitLabUrlFromProject(p, customFieldName);
            if (url is null)
                break;
            var path = PathFromUrl(url);

            list.Add(new ProjectLink(
                RedmineProjectId: id,
                RedmineIdentifier: identifier,
                Name: name,
                GitLabUrl: url,
                GitLabPathWithNs: path
            ));
        }

        return list;
    }




    public async Task<IReadOnlyList<IssueBasic>> GetProjectIssuesBasicAsync(string projectIdOrKey, CancellationToken ct = default)
    {
        var url = $"issues.json?project_id={Uri.EscapeDataString(projectIdOrKey)}&status_id=*&limit=300";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("issues", out var arr)) return Array.Empty<IssueBasic>();

        var list = new List<IssueBasic>(arr.GetArrayLength());
        foreach (var it in arr.EnumerateArray())
        {
            var id = it.GetProperty("id").GetInt32();
            var title = it.TryGetProperty("subject", out var s) ? (s.GetString() ?? "") : "";
            var desc = it.TryGetProperty("description", out var d) ? d.GetString() : null;
            string? tracker = it.TryGetProperty("tracker", out var t) ? (t.TryGetProperty("name", out var tt) ? tt.GetString() : null) : null;
            if (tracker == null || !_trackers.ConvertAll(d => d.ToLower()).Contains(tracker.ToLower()))
                continue;

            var assigneeId = it.TryGetProperty("assigned_to", out var at)
    ? at.TryGetProperty("id", out var aid) ? aid.GetInt32() : (int?)null
    : null;

            var status = it.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn)
                ? sn.GetString()
                : null;



            var dueDate = it.TryGetProperty("due_date", out var dd) && dd.ValueKind == JsonValueKind.String
            ? DateTime.Parse(dd.GetString()!)
            : (DateTime?)null;

            list.Add(new IssueBasic(id, null, title, desc, new List<string>() { tracker }, AssigneeId: assigneeId, dueDate, Status: status));
        }
        return list;

    }


    public async Task SyncGlobalStatusesAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();

        var resp = await _http.GetAsync("issue_statuses.json", ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var s in doc.RootElement.GetProperty("issue_statuses").EnumerateArray())
        {
            var sid = s.GetProperty("id").GetInt32();
            var name = s.GetProperty("name").GetString() ?? "";

            var existing = await db.StatusesRedmine
                .FirstOrDefaultAsync(x => x.RedmineStatusId == sid, ct);

            if (existing is null)
                db.StatusesRedmine.Add(new StatusRedmine { RedmineStatusId = sid, Name = name });
            else
                existing.Name = name;
        }

        await db.SaveChangesAsync(ct);
    }



    public async Task<(bool ok, int id, string message)> CreateIssueAsync(
        string projectIdOrKey,
        string subject,
        string? description = null,
            int? trackerId = null,
            int? assigneeId = null,
            string? sourceUrl = null,
            DateTime? dueDate = null,
            int? statusId = null,
        CancellationToken ct = default)
    {
        var fullDesc = new StringBuilder();
        var cleanDesc = description ?? "";
        var lines = cleanDesc.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(0);
            cleanDesc = string.Join('\n', lines);
        }
        if (!string.IsNullOrEmpty(sourceUrl))
        {
            fullDesc.AppendLine($"Source: {sourceUrl}");
            fullDesc.AppendLine();
        }
        if (!string.IsNullOrEmpty(cleanDesc))
            fullDesc.AppendLine(cleanDesc);



        var issue = new Dictionary<string, object?>
        {
            ["project_id"] = projectIdOrKey,
            ["subject"] = subject,
            ["description"] = fullDesc.ToString(),
            ["due_date"] = dueDate?.ToString("yyyy-MM-dd")

        };
        if (statusId.HasValue)
            issue["status_id"] = statusId.Value;

        if (trackerId.HasValue)
            issue["tracker_id"] = trackerId.Value;

        if (assigneeId.HasValue)
            issue["assigned_to_id"] = assigneeId.Value;

        using var content = new StringContent(JsonSerializer.Serialize(new { issue }), Encoding.UTF8, "application/json");

        var resp = await _http.PostAsync("issues.json", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return (false, 0, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("issue", out var ie)) return (true, ie.GetProperty("id").GetInt32(), "created");
        if (doc.RootElement.TryGetProperty("id", out var idProp)) return (true, idProp.GetInt32(), "created");
        return (false, 0, "Could not parse Redmine create issue response");
    }



    public async Task SyncGlobalTrackersAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();

        var resp = await _http.GetAsync("trackers.json", ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var arr = doc.RootElement.GetProperty("trackers").EnumerateArray();

        foreach (var t in arr)
        {
            var rid = t.GetProperty("id").GetInt32();
            var name = t.GetProperty("name").GetString() ?? "";

            var existing = await db.TrackersRedmine.FirstOrDefaultAsync(x => x.RedmineTrackerId == rid, ct);
            if (existing is null)
                db.TrackersRedmine.Add(new TrackerRedmine { RedmineTrackerId = rid, Name = name });
            else
                existing.Name = name;
        }

        await db.SaveChangesAsync(ct);
    }


    public async Task<IssueBasic> GetSingleIssueBasicAsync(int issueId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"issues/{issueId}.json", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var it = doc.RootElement.GetProperty("issue");

        var title = it.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";
        var desc = it.TryGetProperty("description", out var d) ? d.GetString() : null;

        string? tracker = it.TryGetProperty("tracker", out var t) && t.TryGetProperty("name", out var tn) ? tn.GetString() : null;
        var labels = tracker is null ? null : new List<string> { tracker };

        int? assigneeId = it.TryGetProperty("assigned_to", out var at) && at.TryGetProperty("id", out var aid) ? aid.GetInt32() : (int?)null;
        var status = it.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn) ? sn.GetString() : null;

        DateTime? due = null;
        if (it.TryGetProperty("due_date", out var dd) && dd.ValueKind == JsonValueKind.String)
            due = DateTime.Parse(dd.GetString()!);

        DateTimeOffset? updated = null;
        if (it.TryGetProperty("updated_on", out var uo) && uo.ValueKind == JsonValueKind.String)
            updated = DateTimeOffset.Parse(uo.GetString()!, null, System.Globalization.DateTimeStyles.AssumeUniversal);

        return new IssueBasic(issueId, null, title, desc, labels, assigneeId, due, status, updated);
    }


    public async Task<(bool ok, string message)> UpdateIssueAsync(
        int issueId,
        string? subject = null,
        string? description = null,
        int? trackerId = null,
        int? assigneeId = null,
        DateTime? dueDate = null,
        int? statusId = null,
        CancellationToken ct = default)
    {
        var issue = new Dictionary<string, object?>();
        if (subject is not null) issue["subject"] = subject;
        if (description is not null) issue["description"] = description;
        if (trackerId.HasValue) issue["tracker_id"] = trackerId.Value;
        if (assigneeId.HasValue) issue["assigned_to_id"] = assigneeId.Value;
        if (dueDate.HasValue) issue["due_date"] = dueDate.Value.ToString("yyyy-MM-dd");
        if (statusId.HasValue) issue["status_id"] = statusId.Value;

        using var content = new StringContent(JsonSerializer.Serialize(new { issue }), Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync($"issues/{issueId}.json", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return resp.IsSuccessStatusCode ? (true, "updated")
                                        : (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }


    public async Task<List<RmMember>> GetRedmineMembersAsync(int projID, CancellationToken ct = default)
    {
        var all = new List<RmMember>();
        for (int offset = 0; ; offset += 100)
        {
            var resp = await _http.GetAsync($"/projects/{projID}/memberships.json?limit=100&offset={offset}", ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var memberships = doc.RootElement.GetProperty("memberships").EnumerateArray().ToList();
            foreach (var m in memberships)
            {
                var u = m.GetProperty("user");
                all.Add(new RmMember(u.GetProperty("id").GetInt32(), u.GetProperty("name").GetString() ?? ""));
            }
            var total = doc.RootElement.TryGetProperty("total_count", out var t) ? t.GetInt32() : all.Count;
            if (all.Count >= total || memberships.Count == 0) break;
        }
        return all;
    }



    // -------------------
    // Helpers
    // -------------------
    private static int GetInt(JsonElement e, string name) => e.GetProperty(name).GetInt32();
    private static string? TryGetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string? ExtractGitLabUrlFromProject(JsonElement project, string cfName)
    {
        if (!project.TryGetProperty("custom_fields", out var cfs) || cfs.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var cf in cfs.EnumerateArray())
        {
            var name = TryGetString(cf, "name");
            if (string.Equals(name, cfName, StringComparison.OrdinalIgnoreCase))
            {
                var value = TryGetString(cf, "value");
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        return null;
    }

    private static string? PathFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return path;
    }
}

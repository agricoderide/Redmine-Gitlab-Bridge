using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bridge.Contracts;
using Bridge.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Bridge.Services;

/// <summary>
/// Typed Redmine API client (JSON). Exposes runtime-ready helpers and DTOs.
/// </summary>
public sealed class RedmineClient
{
    private readonly HttpClient _http;
    public readonly RedmineOptions _opt;

    public RedmineClient(HttpClient http, IOptions<RedmineOptions> opt)
    {
        _http = http;
        _opt = opt.Value;

        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // API key in header (preferred)
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            _http.DefaultRequestHeaders.Add("X-Redmine-API-Key", _opt.ApiKey);
    }




    /// <summary>Very simple connectivity check by listing projects.</summary>
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




    /// <summary>
    /// Returns Redmine projects with an optional GitLab link pulled from a custom field (by name).
    /// Also computes GitLab path-with-namespace when a URL is present.
    /// </summary>
    public async Task<IReadOnlyList<ProjectLink>> GetProjectsWithGitLabLinksAsync(
        string customFieldName,
        CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("projects.json?include=custom_fields", ct); //httpResponseMessage
        resp.EnsureSuccessStatusCode();

        // we create a jsondocument based on the received string
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

    // RedmineClient.cs
    public async Task<IReadOnlyList<(int Id, string Name, string? Login, string? Email)>> GetProjectMembersAsync(
        string projectIdOrKey, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"projects/{projectIdOrKey}/memberships.json?limit=100", ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<(int, string, string?, string?)>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("memberships", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<(int, string, string?, string?)>();

        var list = new List<(int, string, string?, string?)>();
        foreach (var m in arr.EnumerateArray())
        {
            if (!m.TryGetProperty("user", out var u) || u.ValueKind != JsonValueKind.Object) continue;
            var id = u.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0;
            var name = u.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
            var login = u.TryGetProperty("login", out var lp) ? lp.GetString() : null;
            var mail = u.TryGetProperty("mail", out var mp) ? mp.GetString() : null;
            if (id != 0) list.Add((id, name, login, mail));
        }
        return list;
    }


    // in RedmineClient.cs

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

            string? trackerName = null;
            if (it.TryGetProperty("tracker", out var tr) && tr.ValueKind == JsonValueKind.Object &&
                tr.TryGetProperty("name", out var tn))
                trackerName = tn.GetString();

            list.Add(new IssueBasic(id, null, title, desc, trackerName, null));
        }
        return list;
    }

    public async Task<IReadOnlyList<(int Id, string Name)>> GetProjectTrackersAsync(
        string projectIdOrKey,
        CancellationToken ct = default)
    {
        // You must include `trackers` explicitly
        var resp = await _http.GetAsync($"projects/{Uri.EscapeDataString(projectIdOrKey)}.json?include=trackers", ct);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<(int, string)>();

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("project", out var p))
        {
            // Optional: log body to see what came back (permissions, wrong id, etc.)
            // _log.LogWarning("Project response missing 'project' for {Project}. Body: {Body}", projectIdOrKey, body);
            return Array.Empty<(int, string)>();
        }

        if (!p.TryGetProperty("trackers", out var tarr) || tarr.ValueKind != JsonValueKind.Array)
        {
            // Optional: log body to diagnose why trackers aren't present
            // _log.LogInformation("No trackers in response for {Project}. Body: {Body}", projectIdOrKey, body);
            return Array.Empty<(int, string)>();
        }

        var list = new List<(int, string)>(tarr.GetArrayLength());
        foreach (var t in tarr.EnumerateArray())
        {
            if (t.TryGetProperty("id", out var idp) && t.TryGetProperty("name", out var np))
                list.Add((idp.GetInt32(), np.GetString() ?? ""));
        }
        return list;
    }


    public async Task<(bool ok, int id, string message)> CreateIssueAsync(
        string projectIdOrKey,
        string subject,
        string? description = null,
        CancellationToken ct = default,
        int? trackerId = null) // <- seeder passes Feature/Bug id here
    {
        var issue = new Dictionary<string, object?>
        {
            ["project_id"] = projectIdOrKey,
            ["subject"] = subject,
            ["description"] = description
        };
        if (trackerId is int tid) issue["tracker_id"] = tid;

        using var content = new StringContent(JsonSerializer.Serialize(new { issue }), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("issues.json", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return (false, 0, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("issue", out var ie)) return (true, ie.GetProperty("id").GetInt32(), "created");
        if (doc.RootElement.TryGetProperty("id", out var idProp)) return (true, idProp.GetInt32(), "created");
        return (false, 0, "Could not parse Redmine create issue response");
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
        // GitLab can have trailing ".git" on URLs; strip it for API lookups
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return path;
    }
}

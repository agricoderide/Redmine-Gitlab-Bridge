using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bridge.Contracts;
using Bridge.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Redmine.Net.Api.Types;

namespace Bridge.Services;

/// <summary>
/// Typed Redmine API client (JSON). Exposes runtime-ready helpers and DTOs.
/// </summary>
public sealed class RedmineClient
{
    public record RmMember(int Id, string Name);

    private readonly HttpClient _http;
    public readonly RedmineOptions _opt;
    public readonly TrackersKeys _trackers;

    public RedmineClient(HttpClient http, IOptions<RedmineOptions> opt, IOptions<TrackersKeys> trackers)
    {
        _http = http;
        _opt = opt.Value;

        _trackers = trackers.Value;

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

    // Redmine: project memberships (id + name)
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
            string? tracker = it.TryGetProperty("tracker", out var t) ? (t.TryGetProperty("name", out var tt) ? tt.GetString() : null) : null;
            if (tracker == null || !_trackers.ConvertAll(d => d.ToLower()).Contains(tracker.ToLower()))
                continue;


            list.Add(new IssueBasic(id, null, title, desc));
        }
        return list;

    }



    public async Task<(bool ok, int id, string message)> CreateIssueAsync(
        string projectIdOrKey,
        string subject,
        string? description = null,
        CancellationToken ct = default) // <- seeder passes Feature/Bug id here
    {
        var issue = new Dictionary<string, object?>
        {
            ["project_id"] = projectIdOrKey,
            ["subject"] = subject,
            ["description"] = description
        };

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

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
    private readonly RedmineOptions _opt;

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
        var resp = await _http.GetAsync("projects.json", ct);
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
            var path = url is null ? null : PathFromUrl(url);

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

    /// <summary>Basic issue list for a project (both open and closed), shaped into IssueBasic.</summary>
    public async Task<IReadOnlyList<IssueBasic>> GetProjectIssuesBasicAsync(
        string projectIdentifierOrId,
        CancellationToken ct = default)
    {
        var url = $"issues.json?project_id={Uri.EscapeDataString(projectIdentifierOrId)}&status_id=*&limit=100";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("issues", out var arr))
            return Array.Empty<IssueBasic>();

        var list = new List<IssueBasic>(arr.GetArrayLength());
        foreach (var it in arr.EnumerateArray())
        {
            var id = GetInt(it, "id");
            var title = TryGetString(it, "subject") ?? "";
            var desc = TryGetString(it, "description");
            list.Add(new IssueBasic(RedmineId: id, GitLabIid: null, Title: title, Description: desc));
        }
        return list;
    }

    /// <summary>Create a Redmine issue using IssueBasic (Title/Description).</summary>
    public async Task<(bool ok, int newRedmineId, string message)> CreateIssueBasicAsync(
        string projectIdentifierOrId,
        IssueBasic issue,
        CancellationToken ct = default)
        => await CreateIssueAsync(projectIdentifierOrId, issue.Title, issue.Description, ct);

    /// <summary>Low-level create issue helper (kept for completeness).</summary>
    public async Task<(bool ok, int id, string message)> CreateIssueAsync(
        string projectIdentifierOrId,
        string subject,
        string? description = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            issue = new
            {
                project_id = projectIdentifierOrId,
                subject,
                description
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("issues.json", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return (false, 0, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

        using var doc = JsonDocument.Parse(body);

        // Most Redmine servers wrap it as { "issue": { ... } }
        if (doc.RootElement.TryGetProperty("issue", out var issueEl))
            return (true, issueEl.GetProperty("id").GetInt32(), "created");

        // Some might return the object directly
        if (doc.RootElement.TryGetProperty("id", out var idProp))
            return (true, idProp.GetInt32(), "created");

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
            var name = TryGetString(cf, "name") ?? TryGetString(cf, "custom_field") ?? "";
            if (!cf.TryGetProperty("value", out var valueEl)) continue;
            var value = valueEl.ValueKind == JsonValueKind.String ? valueEl.GetString() : null;
            if (value is null) continue;

            if (string.Equals(name, cfName, StringComparison.OrdinalIgnoreCase))
            {
                // basic URL sanity
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    return uri.ToString();
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

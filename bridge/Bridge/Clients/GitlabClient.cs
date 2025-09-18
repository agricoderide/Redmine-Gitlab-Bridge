using System.Net.Http.Headers;
using System.Text.Json;
using Bridge.Contracts;
using Bridge.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Bridge.Services;

/// <summary>
/// Typed GitLab API client (v4). Returns runtime-ready DTOs.
/// </summary>
public sealed class GitLabClient
{
    private readonly HttpClient _http;
    private readonly GitLabOptions _opt;

    public GitLabClient(HttpClient http, IOptions<GitLabOptions> opt)
    {
        _http = http;
        _opt = opt.Value;

        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/api/v4/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_opt.PrivateToken))
            _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", _opt.PrivateToken);
    }

    /// <summary>Quick connectivity check.</summary>
    public async Task<(bool ok, string message)> PingAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("projects?per_page=1", ct);
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var arrLen = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.GetArrayLength()
            : 0;
        return (true, $"OK. projects={arrLen}");
    }

    /// <summary>Resolve numeric GitLab project id from a path like "group/subgroup/repo".</summary>
    public async Task<(bool ok, long id, string message)> ResolveProjectIdAsync(
        string pathWithNamespace,
        CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(pathWithNamespace);
        var resp = await _http.GetAsync($"projects/{encoded}", ct);
        if (!resp.IsSuccessStatusCode)
            return (false, 0, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("id").GetInt64();
        return (true, id, "ok");
    }

    /// <summary>
    /// Convenience wrapper; same as ResolveProjectIdAsync but expresses intent.
    /// </summary>
    public Task<(bool ok, long id, string message)> EnsureProjectIdAsync(
        string pathWithNamespace,
        CancellationToken ct = default)
        => ResolveProjectIdAsync(pathWithNamespace, ct);

    /// <summary>List all issues (any state) for a project, shaped to IssueBasic.</summary>
    public async Task<IReadOnlyList<IssueBasic>> GetProjectIssuesBasicAsync(
        long projectId,
        CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"projects/{projectId}/issues?per_page=100&state=all", ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<IssueBasic>();

        var list = new List<IssueBasic>(doc.RootElement.GetArrayLength());
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var iid = e.GetProperty("iid").GetInt64();
            var title = TryGetString(e, "title") ?? "";
            var desc = TryGetString(e, "description");
            list.Add(new IssueBasic(RedmineId: null, GitLabIid: iid, Title: title, Description: desc));
        }
        return list;
    }

    /// <summary>Create a GitLab issue using IssueBasic (Title/Description). Returns new IID.</summary>
    public async Task<(bool ok, long newGitLabIid, string message)> CreateIssueBasicAsync(
        long projectId,
        IssueBasic issue,
        CancellationToken ct = default)
    {
        var (ok, msg, doc) = await CreateIssueAsync(projectId, issue.Title, issue.Description, labelsCsv: null, ct);
        if (!ok || doc is null) return (false, 0, msg);

        var iid = doc.RootElement.GetProperty("iid").GetInt64();
        return (true, iid, "created");
    }

    /// <summary>Low-level create issue (form-url-encoded) that mirrors GitLab docs.</summary>
    public async Task<(bool ok, string message, JsonDocument? json)> CreateIssueAsync(
        long projectId,
        string title,
        string? description = null,
        string? labelsCsv = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string> { ["title"] = title };
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(labelsCsv)) payload["labels"] = labelsCsv;

        using var content = new FormUrlEncodedContent(payload);
        var resp = await _http.PostAsync($"projects/{projectId}/issues", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", null);

        return (true, "created", JsonDocument.Parse(body));
    }

    // -------------
    // Helpers
    // -------------
    private static string? TryGetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() : null;
}

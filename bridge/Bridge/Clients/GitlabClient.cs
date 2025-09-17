using System.Net.Http.Headers;
using System.Text.Json;
using Bridge.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Bridge.Services;

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
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", _opt.PrivateToken);
    }

    // Simple ping: /projects?per_page=1
    public async Task<(bool ok, string message)> PingAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("projects?per_page=1", ct);
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var arrLen = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        return (true, $"OK. projects={arrLen}");
    }

    public async Task<(bool ok, string message, JsonDocument? json)> CreateIssueAsync(
    long projectId,
    string title,
    string? description = null,
    string? labelsCsv = null,
    CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["title"] = title
        };
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(labelsCsv)) payload["labels"] = labelsCsv;

        using var content = new FormUrlEncodedContent(payload);
        var resp = await _http.PostAsync($"projects/{projectId}/issues", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", null);

        var doc = JsonDocument.Parse(body);
        return (true, "created", doc);
    }

    // Resolve numeric project id from path like "group/subgroup/repo"
    public async Task<(bool ok, long id, string message)> ResolveProjectIdAsync(string pathWithNamespace, CancellationToken ct = default)
    {
        // GitLab expects URL-encoded full path
        var encoded = Uri.EscapeDataString(pathWithNamespace);
        var resp = await _http.GetAsync($"projects/{encoded}", ct);
        if (!resp.IsSuccessStatusCode)
            return (false, 0, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("id").GetInt64();
        return (true, id, "ok");
    }

    // List GitLab issues for a project (all states)
    public async Task<IReadOnlyList<JsonElement>> GetProjectIssuesAsync(long projectId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"projects/{projectId}/issues?per_page=100&state=all", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return Array.Empty<JsonElement>();
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}

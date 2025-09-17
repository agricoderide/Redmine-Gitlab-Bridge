using System.Net.Http.Headers;
using System.Text.Json;
using Bridge.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Bridge.Services;

public sealed class RedmineClient
{
    private readonly HttpClient _http;
    private readonly RedmineOptions _opt;

    public RedmineClient(HttpClient http, IOptions<RedmineOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        // Redmine API key sent in header
        _http.DefaultRequestHeaders.Add("X-Redmine-API-Key", _opt.ApiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // Very simple ping: list projects to validate connectivity & key
    public async Task<(bool ok, string message)> PingAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("projects.json?limit=1", ct);
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var count = doc.RootElement.GetProperty("projects").GetArrayLength();
        return (true, $"OK. projects={count}");
    }

    // Services/RedmineClient.cs
    public async Task<IReadOnlyList<JsonElement>> GetProjectsAsync(CancellationToken ct = default)
    {
        // include=custom_fields so the list has the GitLab link CF
        var resp = await _http.GetAsync("projects.json?include=custom_fields", ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("projects", out var arr)) return Array.Empty<JsonElement>();

        // Clone elements so they are independent of the disposed JsonDocument
        return arr.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // Kept for compatibility if ever needed to filter, but not used by seeder anymore
    public async Task<IReadOnlyList<JsonElement>> GetProjectsListAsync(CancellationToken ct = default)
    {
        return await GetProjectsAsync(ct);
    }

    // List issues for a given Redmine project by id or identifier
    public async Task<IReadOnlyList<JsonElement>> GetProjectIssuesAsync(string projectIdentifierOrId, CancellationToken ct = default)
    {
        // include journals/relations not needed for association; fetch basic fields
        // status_id=*, to retrieve open and closed issues
        var url = $"issues.json?project_id={Uri.EscapeDataString(projectIdentifierOrId)}&status_id=*&limit=100";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("issues", out var arr)) return Array.Empty<JsonElement>();

        return arr.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public async Task<JsonElement?> GetProjectDetailsAsync(string identifierOrId, CancellationToken ct = default)
    {
        // single-project endpoint is the most reliable to include custom_fields
        var resp = await _http.GetAsync($"projects/{identifierOrId}.json", ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("project", out var p))
        {
            var clone = p.Clone();
            return clone;
        }
        return null;
    }


}

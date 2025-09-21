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
    public readonly GitLabOptions _opt;

    public GitLabClient(HttpClient http, IOptions<GitLabOptions> opt)
    {
        _http = http;
        _opt = opt.Value;

        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/api/v4/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (string.IsNullOrWhiteSpace(_opt.PrivateToken))
        {
            throw new InvalidOperationException(
                "GitLabClient requires a PrivateToken but none was provided. " +
                "Set GitLab:PrivateToken in configuration."
            );
        }

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



    // in GitLabClient.cs

    public async Task<IReadOnlyList<IssueBasic>> GetProjectIssuesBasicAsync(long projectId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"projects/{projectId}/issues?per_page=300&state=all", ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<IssueBasic>();

        var list = new List<IssueBasic>(doc.RootElement.GetArrayLength());
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var iid = e.GetProperty("iid").GetInt64();
            var title = e.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
            var desc = e.TryGetProperty("description", out var d) ? d.GetString() : null;

            List<string>? labels = null;
            if (e.TryGetProperty("labels", out var labs) && labs.ValueKind == JsonValueKind.Array)
            {
                labels = new List<string>(labs.GetArrayLength());
                foreach (var l in labs.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.String) labels.Add(l.GetString()!);
            }

            list.Add(new IssueBasic(null, iid, title, desc, null, labels));
        }
        return list;
    }





    // GitLabClient.cs
    public async Task<IReadOnlyList<(int Id, string Name, string Username, string? Email)>> GetProjectMembersAsync(
        long projectId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"projects/{projectId}/members/all?per_page=100", ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<(int, string, string, string?)>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<(int, string, string, string?)>();

        var list = new List<(int, string, string, string?)>();
        foreach (var u in doc.RootElement.EnumerateArray())
        {
            var id = u.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0;
            var name = u.TryGetProperty("name", out var np) ? (np.GetString() ?? "") : "";
            var user = u.TryGetProperty("username", out var up) ? up.GetString() : "";
            var mail = u.TryGetProperty("email", out var mp) ? mp.GetString() : null;
            if (id != 0 && !string.IsNullOrEmpty(user))
                list.Add((id, name, user!, mail));
        }
        return list;
    }



    /// <summary>
    /// Create a GitLab issue (Title/Description/Labels). 
    /// Returns new IID if successful.
    /// </summary>
    public async Task<(bool ok, long newGitLabIid, string message)> CreateIssueAsync(
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
            return (false, 0, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

        using var doc = JsonDocument.Parse(body);
        var iid = doc.RootElement.GetProperty("iid").GetInt64();

        return (true, iid, "created");
    }


    // -------------
    // Helpers
    // -------------
    private static string? TryGetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() : null;
}

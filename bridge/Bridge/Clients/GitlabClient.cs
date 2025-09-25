using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bridge.Contracts;
using Bridge.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Bridge.Services;


public sealed class GitLabClient
{
    public record GlMember(int Id, string Username, string Name);
    private readonly HttpClient _http;
    public readonly GitLabOptions _opt;

    private readonly TrackersKeys _trackers;

    public GitLabClient(HttpClient http, IOptions<GitLabOptions> opt, IOptions<TrackersKeys> trackers)
    {
        _http = http;
        _opt = opt.Value;
        _trackers = trackers.Value;

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

    // Connectivity check
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

    // Get all issues from gitlab repo id that respect the trackers from the config
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

            bool hasTracker = false;
            string? label = null;
            if (e.TryGetProperty("labels", out var labs) && labs.ValueKind == JsonValueKind.Array)
            {
                var trackerSet = new HashSet<string>(_trackers, StringComparer.OrdinalIgnoreCase);

                foreach (var l in labs.EnumerateArray())
                {
                    if (l.ValueKind == JsonValueKind.String)
                    {
                        var val = l.GetString();
                        if (!string.IsNullOrWhiteSpace(val) && trackerSet.Contains(val))
                        {
                            hasTracker = true;
                            label = val;
                            break; // one match is enough
                        }
                    }
                }
            }

            var dueDate = e.TryGetProperty("due_date", out var dd) && dd.ValueKind == JsonValueKind.String
                ? DateTime.Parse(dd.GetString()!)
                : (DateTime?)null;

            var state = e.TryGetProperty("state", out var st) ? st.GetString() : null;

            if (hasTracker && label != null)
            {
                int? gitLabAssigneeId = null;
                if (e.TryGetProperty("assignees", out var assignees) && assignees.ValueKind == JsonValueKind.Array)
                {
                    var first = assignees.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var aid))
                        gitLabAssigneeId = aid.GetInt32();
                }
                list.Add(new IssueBasic(null, iid, title, desc, new List<string>() { label }, gitLabAssigneeId, dueDate, Status: state));
            }
        }

        return list;
    }

    public async Task<(bool ok, long newGitLabIid, string message)> CreateIssueAsync(
        long projectId,
        string title,
        string? description = null,
        IEnumerable<string>? labels = null,
        int? assigneeId = null,
        string? sourceUrl = null,
        DateTime? dueDate = null,
string? state = null,
        CancellationToken ct = default)
    {
        #region CreateNewDescription
        var fullDesc = new StringBuilder();
        var cleanDesc = description ?? "";         // remove existing "Source:" if it is the first line
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
        #endregion

        var payload = new Dictionary<string, string?>
        {
            ["title"] = title,
            ["description"] = fullDesc.ToString(),
            ["due_date"] = dueDate?.ToString("yyyy-MM-dd")

        };

        if (labels != null)
            payload["labels"] = string.Join(",", labels);

        if (assigneeId.HasValue)
            payload["assignee_ids"] = assigneeId.Value.ToString();

        using var content = new FormUrlEncodedContent(payload);
        var resp = await _http.PostAsync($"projects/{projectId}/issues", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return (false, 0, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

        using var doc = JsonDocument.Parse(body);
        var iid = doc.RootElement.GetProperty("iid").GetInt64();

        // Close the issue if it was marked as close
        if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase))
        {
            var closeOk = await CloseIssueAsync(projectId, iid, ct);
            if (!closeOk)
                return (true, iid, "created (but failed to close)");
        }


        return (true, iid, "created");
    }

    // Get all the members that belong to the repo
    public async Task<List<GlMember>> GetGitLabProjectMembersAsync(int projectId, CancellationToken ct = default)
    {
        var all = new List<GlMember>();
        for (int page = 1; ; page++)
        {
            var resp = await _http.GetAsync($"/api/v4/projects/{projectId}/members/all?per_page=100&page={page}", ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var arr = doc.RootElement.EnumerateArray().ToList();
            foreach (var m in arr)
            {
                var id = m.GetProperty("id").GetInt32();
                var user = m.GetProperty("username").GetString() ?? "";
                var name = m.GetProperty("name").GetString() ?? "";

                // Do not add if it is a bot user
                if (IsBotUsername(user)) continue;

                all.Add(new GlMember(id, user, name));
            }

            if (arr.Count < 100) break;
        }
        return all;
    }


    // This is used to create a kind of snapshot of the issue and save it to the database
    public async Task<IssueBasic> GetSingleIssueBasicAsync(long projectId, long issueIid, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"projects/{projectId}/issues/{issueIid}", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var e = doc.RootElement;

        var title = e.GetProperty("title").GetString() ?? "";
        var desc = e.TryGetProperty("description", out var d) ? d.GetString() : null;

        List<string>? labels = null;
        if (e.TryGetProperty("labels", out var labs) && labs.ValueKind == JsonValueKind.Array)
            labels = labs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();

        int? assigneeId = null;
        if (e.TryGetProperty("assignees", out var ass) && ass.ValueKind == JsonValueKind.Array)
        {
            var first = ass.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var aid))
                assigneeId = aid.GetInt32();
        }

        DateTime? due = null;
        if (e.TryGetProperty("due_date", out var dd) && dd.ValueKind == JsonValueKind.String)
            due = DateTime.Parse(dd.GetString()!);

        var state = e.TryGetProperty("state", out var st) ? st.GetString() : null;

        DateTimeOffset? updated = null;
        if (e.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.String)
            updated = DateTimeOffset.Parse(ua.GetString()!, null, System.Globalization.DateTimeStyles.AssumeUniversal);

        var iid = e.GetProperty("iid").GetInt64();
        return new IssueBasic(null, iid, title, desc, labels, assigneeId, due, state, updated);
    }



    // Update issue that was found that was a previous version
    public async Task<(bool ok, string message)> UpdateIssueAsync(
        long projectId, long issueIid,
        string? title = null,
        string? description = null,
        IEnumerable<string>? labels = null,
        int? assigneeId = null,
        DateTime? dueDate = null,
        string? state = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>();
        if (title is not null) payload["title"] = title;
        if (description is not null) payload["description"] = description;
        if (labels is not null) payload["labels"] = string.Join(",", labels);
        if (assigneeId.HasValue) payload["assignee_ids"] = new[] { assigneeId.Value };
        if (dueDate.HasValue) payload["due_date"] = dueDate.Value.ToString("yyyy-MM-dd");
        if (!string.IsNullOrEmpty(state))
            payload["state_event"] = string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase) ? "close"
                              : string.Equals(state, "opened", StringComparison.OrdinalIgnoreCase) ? "reopen" : null;

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync($"projects/{projectId}/issues/{issueIid}", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return resp.IsSuccessStatusCode ? (true, "updated")
                                        : (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }


    // Method to close issue
    public async Task<bool> CloseIssueAsync(long projectId, long issueIid, CancellationToken ct = default)
    {
        var payload = new { state_event = "close" };
        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );
        var resp = await _http.PutAsync($"/api/v4/projects/{projectId}/issues/{issueIid}", content, ct);
        return resp.IsSuccessStatusCode;
    }

    // Resolve numeric GitLab project id from a path like "group/subgroup/repo".
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

    static readonly Regex ProjectOrGroupBotRx =
    new(@"^(project|group)_[0-9]+_bot($|_)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static bool IsBotUsername(string? username)
        => !string.IsNullOrEmpty(username) && ProjectOrGroupBotRx.IsMatch(username!);
}

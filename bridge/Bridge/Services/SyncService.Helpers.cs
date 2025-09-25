using System.Security.Cryptography;
using System.Text;
using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{

    // at top of the file (or your helpers file)
    internal static class DescriptionUtils
    {
        public static string AddOrUpdateSourceLink(string? description, string sourceUrl)
        {
            var body = (description ?? string.Empty).Replace("\r\n", "\n").TrimStart('\n');
            if (body.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
            {
                // replace the first line with the new Source
                var nl = body.IndexOf('\n');
                var rest = nl >= 0 ? body[(nl + 1)..].TrimStart('\n') : string.Empty;
                return rest.Length > 0 ? $"Source: {sourceUrl}\n\n{rest}" : $"Source: {sourceUrl}";
            }
            return body.Length > 0 ? $"Source: {sourceUrl}\n\n{body}" : $"Source: {sourceUrl}";
        }
    }

    private async Task<int?> ResolveRedmineStatusIdFromGitLabState(string? glState, CancellationToken ct)
    {
        var target = string.Equals(glState, "closed", StringComparison.OrdinalIgnoreCase) ? "Closed" : "New";
        return await _db.StatusesRedmine.Where(s => s.Name == target)
                 .Select(s => (int?)s.RedmineStatusId).FirstOrDefaultAsync(ct);
    }

    private static string? MapGitLabStateFromRedmine(string? rmStatus)
    {
        if (string.IsNullOrWhiteSpace(rmStatus)) return null;
        return string.Equals(rmStatus, "Closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "opened";
    }

    private async Task<int?> GetTrackerIdByNameAsync(string? trackerName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackerName)) return null;
        trackerName = trackerName.Trim();
        return await _db.TrackersRedmine
            .Where(t => t.Name.ToLower() == trackerName.ToLower())
            .Select(t => (int?)t.RedmineTrackerId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int?> ToRedmineAssigneeAsync(int? gitLabAssigneeId, CancellationToken ct)
    {
        if (!gitLabAssigneeId.HasValue) return null;
        return await _db.Users
            .Where(u => u.GitLabUserId == gitLabAssigneeId.Value)
            .Select(u => (int?)u.RedmineUserId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int?> ToGitLabAssigneeAsync(int? redmineAssigneeId, CancellationToken ct)
    {
        if (!redmineAssigneeId.HasValue) return null;
        return await _db.Users
            .Where(u => u.RedmineUserId == redmineAssigneeId.Value)
            .Select(u => (int?)u.GitLabUserId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task SeedMappingsByTitleAsync(
        ProjectSync p,
        long glId,
        List<IssueBasic> rmIssues,
        List<IssueBasic> glIssues,
        HashSet<int> mappedRm,
        HashSet<long> mappedGl,
        CancellationToken ct)
    {
        var glByTitle = glIssues
            .Where(i => !string.IsNullOrWhiteSpace(i.Title))
            .GroupBy(i => i.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var rmi in rmIssues)
        {
            if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) continue;
            var title = rmi.Title?.Trim();
            if (string.IsNullOrEmpty(title)) continue;

            if (!glByTitle.TryGetValue(title, out var cands) || cands.Count != 1) continue;

            var gl = cands[0];
            if (gl.GitLabIid is not long giid || mappedGl.Contains(giid)) continue;

            var mapping = new IssueMapping
            {
                ProjectSyncId = p.Id,
                RedmineIssueId = rmId,
                GitLabIssueId = giid,
            };
            _db.IssueMappings.Add(mapping);

            var glCurrent = await _gitlab.GetSingleIssueBasicAsync(glId, giid, ct);
            await PatchRedmineFromGitLabAsync(p, mapping, glCurrent, ct);
            mapping.CanonicalSnapshot = glCurrent;

            mappedRm.Add(rmId);
            mappedGl.Add(giid);
        }
    }

    private async Task CreateMissingFromRedmineAsync(
        ProjectSync p,
        long glId,
        List<IssueBasic> rmIssues,
        HashSet<int> mappedRm,
        HashSet<long> mappedGl,
        CancellationToken ct)
    {
        foreach (var rmi in rmIssues)
        {
            await CreateGitLabFromRedmineAsync(p, glId, rmi, mappedRm, mappedGl, ct);
        }
    }

    private async Task CreateMissingFromGitLabAsync(
        ProjectSync p,
        List<IssueBasic> glIssues,
        HashSet<int> mappedRm,
        HashSet<long> mappedGl,
        CancellationToken ct)
    {
        foreach (var gli in glIssues)
        {
            await CreateRedmineFromGitLabAsync(p, gli, mappedRm, mappedGl, ct);
        }
    }

    private async Task ReconcileMappingsAsync(
        ProjectSync p,
        List<IssueBasic> rmIssues,
        List<IssueBasic> glIssues,
        CancellationToken ct)
    {
        var glByIid = glIssues.Where(x => x.GitLabIid.HasValue)
                              .ToDictionary(i => i.GitLabIid!.Value, i => i);
        var rmById = rmIssues.Where(x => x.RedmineId.HasValue)
                              .ToDictionary(i => i.RedmineId!.Value, i => i);

        var mappings = await _db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .ToListAsync(ct);

        foreach (var m in mappings)
        {
            glByIid.TryGetValue(m.GitLabIssueId, out var glHint);
            rmById.TryGetValue(m.RedmineIssueId, out var rmHint);
            await ProcessPairByCanonicalAsync(p, m, ct, glHint, rmHint);
        }
    }






    public static string ExtractSearchKey(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "";

        username = username.Trim();

        // if username has separators like john.prior → take last part
        var parts = username.Split(new[] { '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
            return parts[^1];

        // compact handles like "rprior" → drop the first letter if long enough
        if (username.Length >= 4)
            return username.Substring(1);

        return username;
    }

    public static string Compute(IssueBasic i)
    {
        static string N(string? s) => s ?? "";
        var parts = new[]
        {
            N(i.Title),
            N(i.Description),
            string.Join(",", (i.Labels ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            i.AssigneeId?.ToString() ?? "",
            i.DueDate?.ToString("yyyy-MM-dd") ?? "",
            N(i.Status)
        };
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    // ---- normalization & equality ---------------------------------------------
    private static IReadOnlyList<string> NormalizeLabels(List<string>? labels) =>
        (labels ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool LabelsEqual(List<string>? a, List<string>? b) =>
        NormalizeLabels(a).SequenceEqual(NormalizeLabels(b), StringComparer.OrdinalIgnoreCase);

    public static bool ValueEquals(IssueBasic a, IssueBasic b)
    {
        if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Description ?? "", b.Description ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Status ?? "", b.Status ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Nullable.Equals(a.AssigneeId, b.AssigneeId)) return false;
        if (!Nullable.Equals(a.DueDate, b.DueDate)) return false;
        if (!LabelsEqual(a.Labels, b.Labels)) return false;
        return true;
    }
}

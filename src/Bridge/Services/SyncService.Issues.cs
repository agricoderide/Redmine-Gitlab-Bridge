using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{
    private async Task SyncIssuesAsync(ProjectSync p, long glId, CancellationToken ct)
    {
        if (p is null || p.GitLabProject is null) return;

        // Every issue that is on both redmine and gitlab, but not in the table
        var rmIssues = (await _redmine.GetProjectIssuesBasicAsync(p.RedmineIdentifier, ct)).ToList();
        var glIssues = (await _gitlab.GetProjectIssuesBasicAsync(glId, ct)).ToList();

        // 2) Load existing mappings for this project
        var mappedRm = new HashSet<int>(_db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .Select(m => m.RedmineIssueId));

        var mappedGl = new HashSet<long>(_db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .Select(m => m.GitLabIssueId));

        // 3) Seed 1:1 mappings by exact title that are not already in the database
        await SeedMappingsByTitleAsync(p, glId, rmIssues, glIssues, mappedRm, mappedGl, ct);
        await _db.SaveChangesAsync(ct);

        await CheckIfIssuesFromTheTableStillExist(p, glId, rmIssues, glIssues, mappedRm, mappedGl, ct);
        await _db.SaveChangesAsync(ct);

        // 4) Create missing issues on each side
        await CreateMissingFromRedmineAsync(p, glId, rmIssues, mappedRm, mappedGl, ct);
        await _db.SaveChangesAsync(ct);

        await CreateMissingFromGitLabAsync(p, glIssues, mappedRm, mappedGl, ct);
        await _db.SaveChangesAsync(ct);

        // 5) Reconcile all mapped pairs by canonical snapshot
        await ReconcileMappingsAsync(p, rmIssues, glIssues, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task CheckIfIssuesFromTheTableStillExist(
        ProjectSync p,
        long glId,
        List<IssueBasic> rmIssues,
        List<IssueBasic> glIssues,
        HashSet<int> mappedRm,
        HashSet<long> mappedGl,
        CancellationToken ct)
    {
        // Fast existence sets from the lists we already fetched
        var rmExisting = rmIssues
            .Where(i => i.RedmineId is not null)
            .Select(i => i.RedmineId!.Value)
            .ToHashSet();

        var glExisting = glIssues
            .Where(i => i.GitLabIid is not null)
            .Select(i => i.GitLabIid!.Value)
            .ToHashSet();

        // Load mappings for this project
        var mappings = await _db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .ToListAsync(ct);

        foreach (var m in mappings.ToList())
        {
            ct.ThrowIfCancellationRequested();

            bool rmExists = rmExisting.Contains(m.RedmineIssueId);
            bool glExists = glExisting.Contains(m.GitLabIssueId);

            // Double-check with API (handles any paging or transient listing gaps)
            if (!rmExists)
            {
                var rm = await _redmine.TryGetSingleIssueBasicAsync(m.RedmineIssueId, ct);
                rmExists = rm is not null;
            }

            if (!glExists)
            {
                var gl = await _gitlab.TryGetSingleIssueBasicAsync(glId, m.GitLabIssueId, ct);
                glExists = gl is not null;
            }

            // If either side is gone, delete the mapping row (your chosen behavior)
            if (!rmExists || !glExists)
            {
                _db.IssueMappings.Remove(m);
                mappedRm.Remove(m.RedmineIssueId);
                mappedGl.Remove(m.GitLabIssueId);

                _log.LogInformation(
                    "Removed mapping P{ProjectId} RM#{RmId} ⇄ GL!{GlIid} because {Side} issue no longer exists.",
                    p.Id,
                    m.RedmineIssueId,
                    m.GitLabIssueId,
                    (!rmExists && !glExists) ? "both" : (!rmExists ? "Redmine" : "GitLab"));
            }
        }
    }




    private async Task CreateRedmineFromGitLabAsync(ProjectSync p, IssueBasic gli, HashSet<int> mappedRm, HashSet<long> mappedGl, CancellationToken ct)
    {
        if (gli.GitLabIid is not long giid || mappedGl.Contains(giid))
            return;

        var trackerName = gli.Labels?.FirstOrDefault() ?? "Feature";
        int? trackerId = null;

        if (trackerName != null)
        {
            trackerId = await _db.TrackersRedmine
                .Where(t => t.Name.ToLower() == trackerName.ToLower())
                .Select(t => (int?)t.RedmineTrackerId)
                .FirstOrDefaultAsync(ct);
        }

        var rmAssigneeId = gli.AssigneeId.HasValue
            ? await _db.Users
                .Where(u => u.GitLabUserId == gli.AssigneeId.Value)
                .Select(u => (int?)u.RedmineUserId)
                .FirstOrDefaultAsync(ct)
            : null;

        if (p == null || p.GitLabProject == null) return;
        var gitlabUrl = $"{p.GitLabProject.Url}/-/issues/{gli.GitLabIid}";

        int? statusId = null;
        if (string.Equals(gli.Status, "closed", StringComparison.OrdinalIgnoreCase))
        {
            statusId = await _db.StatusesRedmine
                .Where(s => s.Name == "Closed")
                .Select(s => (int?)s.RedmineStatusId)
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            statusId = await _db.StatusesRedmine
                .Where(s => s.Name == "New")
                .Select(s => (int?)s.RedmineStatusId)
                .FirstOrDefaultAsync(ct);
        }

        var (rok, newRmId, rmsg) = await _redmine.CreateIssueAsync(
            p.RedmineIdentifier,
            gli.Title,
            gli.Description,
            trackerId,
            rmAssigneeId,
            gitlabUrl,
            gli.DueDate,
            statusId,
            ct
        );

        if (!rok)
        {
            _log.LogWarning("Redmine create issue failed for project {RmKey}: {Msg}", p.RedmineIdentifier, rmsg);
            return;
        }

        var mapping = new IssueMapping
        {
            ProjectSyncId = p.Id,
            RedmineIssueId = newRmId,
            GitLabIssueId = giid,
        };
        _db.IssueMappings.Add(mapping);

        // To compute the hashvalue
        var glCurrent = await _gitlab.TryGetSingleIssueBasicAsync(p.GitLabProject!.GitLabProjectId!.Value, giid, ct);
        mapping.CanonicalSnapshot = glCurrent;

        mappedGl.Add(giid);
        mappedRm.Add(newRmId);
    }





    private async Task CreateGitLabFromRedmineAsync(ProjectSync p, long glId, IssueBasic rmi, HashSet<int> mappedRm, HashSet<long> mappedGl, CancellationToken ct)
    {
        if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) return;

        var labels = rmi.Labels ?? new List<string>();

        var glAssigneeId = rmi.AssigneeId.HasValue
            ? await _db.Users
                .Where(u => u.RedmineUserId == rmi.AssigneeId.Value)
                .Select(u => (int?)u.GitLabUserId)
                .FirstOrDefaultAsync(ct)
            : null;

        var redmineUrl = $"{_redmine._opt.PublicUrl}/issues/{rmId}";

        var glState = string.Equals(rmi.Status, "Closed", StringComparison.OrdinalIgnoreCase)
            ? "closed"
            : "opened";

        var (ok, newIid, msg) = await _gitlab.CreateIssueAsync(
            glId,
            rmi.Title,
            rmi.Description,
            labels,
            glAssigneeId,
            redmineUrl,
            rmi.DueDate,
            glState,
            ct
        );
        if (!ok)
        {
            _log.LogWarning("GitLab create issue failed for project {GlProjectId}: {Msg}", glId, msg);
            return;
        }

        var mapping = new IssueMapping
        {
            ProjectSyncId = p.Id,
            RedmineIssueId = rmId,
            GitLabIssueId = newIid,
        };
        _db.IssueMappings.Add(mapping);

        // INIT canonical to RM
        var rmCurrent = await _redmine.TryGetSingleIssueBasicAsync(rmId, ct);
        mapping.CanonicalSnapshot = rmCurrent;

        mappedRm.Add(rmId);
        mappedGl.Add(newIid);
    }

    private async Task SeedMappingsByTitleAsync(ProjectSync p, long glId, List<IssueBasic> rmIssues, List<IssueBasic> glIssues, HashSet<int> mappedRm, HashSet<long> mappedGl, CancellationToken ct)
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

            // To compute the hash value
            var glCurrent = await _gitlab.TryGetSingleIssueBasicAsync(glId, giid, ct);
            await PatchRedmineFromGitLabAsync(p, mapping, glCurrent, ct);
            mapping.CanonicalSnapshot = glCurrent;

            mappedRm.Add(rmId);
            mappedGl.Add(giid);
        }
    }

    private async Task CreateMissingFromRedmineAsync(ProjectSync p, long glId, List<IssueBasic> rmIssues, HashSet<int> mappedRm, HashSet<long> mappedGl, CancellationToken ct)
    {
        foreach (var rmi in rmIssues)
        {
            await CreateGitLabFromRedmineAsync(p, glId, rmi, mappedRm, mappedGl, ct);
        }
    }

    private async Task CreateMissingFromGitLabAsync(ProjectSync p, List<IssueBasic> glIssues, HashSet<int> mappedRm, HashSet<long> mappedGl, CancellationToken ct)
    {
        foreach (var gli in glIssues)
        {
            await CreateRedmineFromGitLabAsync(p, gli, mappedRm, mappedGl, ct);
        }
    }


}

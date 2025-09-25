using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{
    private async Task SyncIssuesAsync(ProjectSync p, long glId, CancellationToken ct)
    {
        if (p is null || p.GitLabProject is null) return;

        var rmIssues = (await _redmine.GetProjectIssuesBasicAsync(p.RedmineIdentifier, ct)).ToList();
        var glIssues = (await _gitlab.GetProjectIssuesBasicAsync(glId, ct)).ToList();

        // 2) Load existing mappings for this project
        var mappedRm = new HashSet<int>(_db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .Select(m => m.RedmineIssueId));

        var mappedGl = new HashSet<long>(_db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .Select(m => m.GitLabIssueId));

        // 3) Seed 1:1 mappings by exact title (your current heuristic)
        await SeedMappingsByTitleAsync(p, glId, rmIssues, glIssues, mappedRm, mappedGl, ct);
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

        // INIT canonical to GL
        var glCurrent = await _gitlab.GetSingleIssueBasicAsync(p.GitLabProject!.GitLabProjectId!.Value, giid, ct);
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

        var redmineUrl = $"{_redmine._opt.BaseUrl}/issues/{rmId}";

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
        var rmCurrent = await _redmine.GetSingleIssueBasicAsync(rmId, ct);
        mapping.CanonicalSnapshot = rmCurrent;

        mappedRm.Add(rmId);
        mappedGl.Add(newIid);
    }
}

using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{
    private static IssueBasic MergePerFieldByUpdated(IssueBasic gl, IssueBasic rm)
    {
        bool glNewer = (gl.UpdatedAtUtc ?? DateTimeOffset.MinValue) >= (rm.UpdatedAtUtc ?? DateTimeOffset.MinValue);

        string title = glNewer ? gl.Title : rm.Title;
        string? desc = glNewer ? gl.Description : rm.Description;
        List<string>? labs = glNewer ? gl.Labels : rm.Labels;
        int? assignee = glNewer ? gl.AssigneeId : rm.AssigneeId;
        DateTime? due = glNewer ? gl.DueDate : rm.DueDate;
        string? status = glNewer ? gl.Status : rm.Status;
        var updated = (gl.UpdatedAtUtc >= rm.UpdatedAtUtc) ? gl.UpdatedAtUtc : rm.UpdatedAtUtc;

        return new IssueBasic(rm.RedmineId, gl.GitLabIid, title, desc, labs, assignee, due, status, updated);
    }

    public async Task ProcessPairByCanonicalAsync(ProjectSync p, IssueMapping m, CancellationToken ct, IssueBasic? glHint = null, IssueBasic? rmHint = null)
    {
        // Always use TryGet to avoid exceptions on 404 and to detect deletions safely
        var gl = glHint ?? await _gitlab.TryGetSingleIssueBasicAsync(
            p.GitLabProject!.GitLabProjectId!.Value, m.GitLabIssueId, ct);

        var rm = rmHint ?? await _redmine.TryGetSingleIssueBasicAsync(
            m.RedmineIssueId, ct);

        // If either side no longer exists, just remove the stale mapping (your chosen behavior)
        if (gl is null || rm is null)
        {
            _db.IssueMappings.Remove(m);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Removed stale mapping P{Pid} RM#{Rm} ⇄ GL!{Gl} (missing {Side}).",
                p.Id, m.RedmineIssueId, m.GitLabIssueId, gl is null && rm is null ? "both" : gl is null ? "GitLab" : "Redmine");
            return;
        }

        // --- Normalize “Source:” links so repo-URL changes are detected and patched
        var redmineUrl = $"{_redmine._opt.PublicUrl.TrimEnd('/')}/issues/{m.RedmineIssueId}";
        var gitlabUrl = $"{p.GitLabProject!.Url}/-/issues/{m.GitLabIssueId}";

        gl = gl with { Description = DescriptionUtils.AddOrUpdateSourceLink(gl.Description, redmineUrl) };
        rm = rm with { Description = DescriptionUtils.AddOrUpdateSourceLink(rm.Description, gitlabUrl) };

        var canon = m.CanonicalSnapshot;

        if (canon is null)
        {
            await PatchRedmineFromGitLabAsync(p, m, gl, ct);
            m.CanonicalSnapshot = gl;
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Compare to canonical (now safe: gl/rm are non-null)
        bool glEqualsCanon = ValueEquals(gl, canon);
        bool rmEqualsCanon = ValueEquals(rm, canon);

        if (glEqualsCanon && rmEqualsCanon) return;

        if (!glEqualsCanon && rmEqualsCanon)
        {
            await PatchRedmineFromGitLabAsync(p, m, gl, ct);
            m.CanonicalSnapshot = gl;
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (glEqualsCanon && !rmEqualsCanon)
        {
            await PatchGitLabFromRedmineAsync(p, m, rm, ct);
            m.CanonicalSnapshot = rm;
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Conflict: merge by UpdatedAt (existing logic)
        var winner = MergePerFieldByUpdated(gl, rm);

        var glPatch = await BuildGitLabPatchAsync(gl, winner, ct);
        await ApplyGitLabPatchAsync(p, m.GitLabIssueId, glPatch, ct);

        var rmPatch = await BuildRedminePatchAsync(rm, winner, ct);
        await ApplyRedminePatchAsync(m.RedmineIssueId, rmPatch, ct);

        m.CanonicalSnapshot = winner;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ReconcileMappingsAsync(ProjectSync p, List<IssueBasic> rmIssues, List<IssueBasic> glIssues, CancellationToken ct)
    {
        // indexes live issues by their ids
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
}

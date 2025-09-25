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
        // Get both issues and compare them to the canonical
        var gl = glHint;
        if (gl is null || gl.UpdatedAtUtc is null)
            gl = await _gitlab.GetSingleIssueBasicAsync(p.GitLabProject!.GitLabProjectId!.Value, m.GitLabIssueId, ct);

        var rm = rmHint;
        if (rm is null || rm.UpdatedAtUtc is null)
            rm = await _redmine.GetSingleIssueBasicAsync(m.RedmineIssueId, ct);

        var canon = m.CanonicalSnapshot;

        if (canon is null)
        {
            await PatchRedmineFromGitLabAsync(p, m, gl!, ct);
            m.CanonicalSnapshot = gl!;
            await _db.SaveChangesAsync(ct);
            return;
        }

        bool glEqualsCanon = ValueEquals(gl!, canon);
        bool rmEqualsCanon = ValueEquals(rm!, canon);

        if (glEqualsCanon && rmEqualsCanon) return;

        // Patch the differences from the canonical to the other side
        if (!glEqualsCanon && rmEqualsCanon)
        {
            await PatchRedmineFromGitLabAsync(p, m, gl!, ct);
            m.CanonicalSnapshot = gl!;
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (glEqualsCanon && !rmEqualsCanon)
        {
            await PatchGitLabFromRedmineAsync(p, m, rm!, ct);
            m.CanonicalSnapshot = rm!;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var winner = MergePerFieldByUpdated(gl!, rm!);

        var glPatch = await BuildGitLabPatchAsync(gl!, winner, ct);
        await ApplyGitLabPatchAsync(p, m.GitLabIssueId, glPatch, ct);

        var rmPatch = await BuildRedminePatchAsync(rm!, winner, ct);
        await ApplyRedminePatchAsync(m.RedmineIssueId, rmPatch, ct);

        m.CanonicalSnapshot = winner;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ReconcileMappingsAsync(ProjectSync p, List<IssueBasic> rmIssues, List<IssueBasic> glIssues, CancellationToken ct)
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
}

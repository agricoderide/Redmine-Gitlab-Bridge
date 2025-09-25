using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{
    private async Task PatchRedmineFromGitLabAsync(ProjectSync p, IssueMapping m, IssueBasic gl, CancellationToken ct)
    {
        var gitlabUrl = $"{p.GitLabProject!.Url}/-/issues/{m.GitLabIssueId}";

        var trackerName = gl.Labels?.FirstOrDefault();
        var rmTarget = new IssueBasic(
            RedmineId: m.RedmineIssueId,
            GitLabIid: gl.GitLabIid,
            Title: gl.Title,
            Description: DescriptionUtils.AddOrUpdateSourceLink(gl.Description, gitlabUrl),
            Labels: trackerName is null ? null : new List<string> { trackerName },
            AssigneeId: await ToRedmineAssigneeAsync(gl.AssigneeId, ct),
            DueDate: gl.DueDate,
            Status: string.Equals(gl.Status, "closed", StringComparison.OrdinalIgnoreCase) ? "Closed" : "New",
            UpdatedAtUtc: gl.UpdatedAtUtc
        );

        var rmCurrent = await _redmine.TryGetSingleIssueBasicAsync(m.RedmineIssueId, ct);
        var patch = await BuildRedminePatchAsync(rmCurrent, rmTarget, ct);
        await ApplyRedminePatchAsync(m.RedmineIssueId, patch, ct);
    }

    private async Task PatchGitLabFromRedmineAsync(ProjectSync p, IssueMapping m, IssueBasic rm, CancellationToken ct)
    {

        var redmineUrl = $"{_redmine._opt.BaseUrl.TrimEnd('/')}/issues/{m.RedmineIssueId}";

        var glTarget = new IssueBasic(
            RedmineId: rm.RedmineId,
            GitLabIid: m.GitLabIssueId,
            Title: rm.Title,
            Description: DescriptionUtils.AddOrUpdateSourceLink(rm.Description, redmineUrl),
            Labels: rm.Labels,
            AssigneeId: rm.AssigneeId,
            DueDate: rm.DueDate,
            Status: rm.Status,
            UpdatedAtUtc: rm.UpdatedAtUtc
        );

        var glCurrent = await _gitlab.TryGetSingleIssueBasicAsync(p.GitLabProject!.GitLabProjectId!.Value, m.GitLabIssueId, ct);
        var patch = await BuildGitLabPatchAsync(glCurrent, glTarget, ct);
        await ApplyGitLabPatchAsync(p, m.GitLabIssueId, patch, ct);
    }

    private sealed record RedminePatch(
        string? Subject = null,
        string? Description = null,
        int? TrackerId = null,
        int? AssigneeId = null,
        DateTime? DueDate = null,
        int? StatusId = null);

    private sealed record GitLabPatch(
        string? Title = null,
        string? Description = null,
        IEnumerable<string>? Labels = null,
        int? AssigneeId = null,
        DateTime? DueDate = null,
        string? State = null);

    private async Task<RedminePatch> BuildRedminePatchAsync(IssueBasic current, IssueBasic target, CancellationToken ct)
    {
        int? trackerId = null;
        var tName = target.Labels?.FirstOrDefault();
        if (!LabelsEqual(current.Labels, target.Labels))
        {
            if (!string.IsNullOrWhiteSpace(tName))
            {
                trackerId = await _db.TrackersRedmine
                    .Where(t => t.Name.ToLower() == tName!.ToLower())
                    .Select(t => (int?)t.RedmineTrackerId)
                    .FirstOrDefaultAsync(ct);
            }
        }

        int? assigneeId = null;
        if ((current.AssigneeId ?? 0) != (target.AssigneeId ?? 0))
            assigneeId = target.AssigneeId;

        int? statusId = null;
        if (!string.Equals(current.Status ?? "", target.Status ?? "", StringComparison.OrdinalIgnoreCase))
        {
            statusId = string.Equals(target.Status, "Closed", StringComparison.OrdinalIgnoreCase)
                ? await _db.StatusesRedmine.Where(s => s.Name == "Closed").Select(s => (int?)s.RedmineStatusId).FirstOrDefaultAsync(ct)
                : await _db.StatusesRedmine.Where(s => s.Name == "New").Select(s => (int?)s.RedmineStatusId).FirstOrDefaultAsync(ct);
        }

        return new RedminePatch(
            Subject: !string.Equals(current.Title, target.Title, StringComparison.Ordinal) ? target.Title : null,
            Description: !string.Equals(current.Description ?? "", target.Description ?? "", StringComparison.Ordinal) ? target.Description : null,
            TrackerId: trackerId,
            AssigneeId: assigneeId,
            DueDate: !Nullable.Equals(current.DueDate, target.DueDate) ? target.DueDate : null,
            StatusId: statusId
        );
    }

    private async Task<GitLabPatch> BuildGitLabPatchAsync(IssueBasic current, IssueBasic target, CancellationToken ct)
    {
        IEnumerable<string>? labels = null;
        if (!LabelsEqual(current.Labels, target.Labels))
            labels = target.Labels;

        int? glAssignee = null;
        if ((current.AssigneeId ?? 0) != (target.AssigneeId ?? 0))
            glAssignee = await ToGitLabAssigneeAsync(target.AssigneeId, ct);

        string? state = null;
        if (!string.Equals(current.Status ?? "", target.Status ?? "", StringComparison.OrdinalIgnoreCase))
            state = MapGitLabStateFromRedmine(target.Status);

        return new GitLabPatch(
            Title: !string.Equals(current.Title, target.Title, StringComparison.Ordinal) ? target.Title : null,
            Description: !string.Equals(current.Description ?? "", target.Description ?? "", StringComparison.Ordinal) ? target.Description : null,
            Labels: labels,
            AssigneeId: glAssignee,
            DueDate: !Nullable.Equals(current.DueDate, target.DueDate) ? target.DueDate : null,
            State: state
        );
    }

    private async Task ApplyRedminePatchAsync(int redmineIssueId, RedminePatch patch, CancellationToken ct)
    {
        if (patch is { Subject: null, Description: null, TrackerId: null, AssigneeId: null, DueDate: null, StatusId: null })
            return;

        var (ok, msg) = await _redmine.UpdateIssueAsync(
            redmineIssueId,
            subject: patch.Subject,
            description: patch.Description,
            trackerId: patch.TrackerId,
            assigneeId: patch.AssigneeId,
            dueDate: patch.DueDate,
            statusId: patch.StatusId,
            ct: ct
        );
        if (!ok) _log.LogWarning("RM update failed #{Rm}: {Msg}", redmineIssueId, msg);
    }

    private async Task ApplyGitLabPatchAsync(ProjectSync p, long gitLabIid, GitLabPatch patch, CancellationToken ct)
    {
        if (patch is { Title: null, Description: null, Labels: null, AssigneeId: null, DueDate: null, State: null })
            return;

        var (ok, msg) = await _gitlab.UpdateIssueAsync(
            p.GitLabProject!.GitLabProjectId!.Value,
            gitLabIid,
            title: patch.Title,
            description: patch.Description,
            labels: patch.Labels,
            assigneeId: patch.AssigneeId,
            dueDate: patch.DueDate,
            state: patch.State,
            ct: ct
        );
        if (!ok) _log.LogWarning("GL update failed !{Gl}: {Msg}", gitLabIid, msg);
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

    private static string? MapGitLabStateFromRedmine(string? rmStatus)
    {
        if (string.IsNullOrWhiteSpace(rmStatus)) return null;
        return string.Equals(rmStatus, "Closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "opened";
    }

}

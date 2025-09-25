using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{
    private async Task GetRedmine_GitlabProjects(CancellationToken ct)
    {
        var listProjects = await _redmine.GetProjectsWithGitLabLinksAsync(_redmine._opt.GitlabCustomField, ct);

        foreach (var goodProjWithGitlab in listProjects)
        {
            var proj = await _db.Projects
                .Include(p => p.GitLabProject)
                .SingleOrDefaultAsync(p => p.RedmineProjectId == goodProjWithGitlab.RedmineProjectId, ct);

            if (proj is null)
            {
                proj = new ProjectSync
                {
                    RedmineProjectId = goodProjWithGitlab.RedmineProjectId,
                    RedmineIdentifier = goodProjWithGitlab.RedmineIdentifier
                };
                _db.Projects.Add(proj);
            }
            else
            {
                proj.RedmineIdentifier = goodProjWithGitlab.RedmineIdentifier;
            }

            var url = goodProjWithGitlab.GitLabUrl;
            var path = goodProjWithGitlab.GitLabPathWithNs;

            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(path))
            {
                proj.GitLabProject ??= new GitLabProject();

                // update fields (only when they differ)
                if (!StringComparer.Ordinal.Equals(proj.GitLabProject.Url, url) ||
                    !StringComparer.Ordinal.Equals(proj.GitLabProject.PathWithNamespace, path))
                {
                    proj.GitLabProject.Url = url!;
                    proj.GitLabProject.PathWithNamespace = path!;
                }
            }

            if (proj.GitLabProject?.GitLabProjectId is null &&
                !string.IsNullOrWhiteSpace(proj.GitLabProject?.PathWithNamespace))
            {
                var (ok, id, msg) = await _gitlab.ResolveProjectIdAsync(proj.GitLabProject!.PathWithNamespace!, ct);
                if (ok)
                {
                    proj.GitLabProject!.GitLabProjectId = id;
                }
                else
                {
                    _log.LogWarning("Resolve GitLab id failed for {Path}: {Msg}",
                        proj.GitLabProject!.PathWithNamespace, msg);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}

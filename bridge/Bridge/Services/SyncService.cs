using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed class SyncService
{
    private readonly RedmineClient _redmine;
    private readonly GitLabClient _gitlab;
    private readonly SyncDbContext _db;
    private readonly ILogger<SyncService> _log;

    public SyncService(RedmineClient redmine, GitLabClient gitlab, SyncDbContext db, ILogger<SyncService> log)
    {
        _redmine = redmine;
        _gitlab = gitlab;
        _db = db;
        _log = log;
    }

    /// <summary>One full, idempotent sync pass. Call this every poll tick.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        // 0) Ensure reference data is fresh (idempotent)
        await _db.Database.MigrateAsync(ct);
        await _redmine.SyncGlobalTrackersAsync(ct);
        await _redmine.SyncGlobalStatusesAsync(ct);

        // 1) Discover/refresh projects from Redmine (with GitLab link) and resolve GitLab numeric id
        await SyncProjectsAsync(ct);

        // 2) For each project: sync members + issues incrementally
        var projects = await _db.Projects.AsNoTracking().Include(p => p.GitLabProject).ToListAsync(ct);
        foreach (var p in projects)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (p.GitLabProject?.GitLabProjectId is not long glId) continue;

                await SyncMembersAsync(p, ct);
                await SyncIssuesAsync(p, glId, ct);   // put your existing issues logic here, later make incremental
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sync failed per project {Pid}", p.Id);
            }
        }
    }

    private async Task SyncProjectsAsync(CancellationToken ct)
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
                // create the child if it doesn't exist
                proj.GitLabProject ??= new GitLabProject();

                // update fields (only when they differ)
                if (!StringComparer.Ordinal.Equals(proj.GitLabProject.Url, url) ||
                    !StringComparer.Ordinal.Equals(proj.GitLabProject.PathWithNamespace, path))
                {
                    proj.GitLabProject.Url = url!;
                    proj.GitLabProject.PathWithNamespace = path!;
                }
            }



            // 4) Resolve numeric GitLab project ID when we have a path
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

    private async Task SyncMembersAsync(ProjectSync p, CancellationToken ct)
    {
        try
        {
            if (p.GitLabProject is null || p.GitLabProject.GitLabProjectId is null)
                return;

            var gitlabUsers = await _gitlab.GetGitLabProjectMembersAsync((int)p.GitLabProject.GitLabProjectId);
            var redmineUsers = await _redmine.GetRedmineMembersAsync(p.Id);

            var seen = new HashSet<(int rmId, int glId)>();
            var toAdd = new List<User>();

            foreach (var gl in gitlabUsers)
            {
                var key = Helpers.ExtractSearchKey(gl.Username);

                foreach (var rm in redmineUsers)
                {
                    if (rm.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (seen.Add((rm.Id, gl.Id)))
                        {
                            bool exists = await _db.Users.AnyAsync(u => u.RedmineUserId == rm.Id);
                            if (!exists)
                            {
                                toAdd.Add(new User
                                {
                                    RedmineUserId = rm.Id,
                                    GitLabUserId = gl.Id,
                                    Username = gl.Username
                                });
                            }
                        }
                    }
                }
            }

            if (toAdd.Count > 0)
            {
                _db.Users.AddRange(toAdd);
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task SyncIssuesAsync(ProjectSync p, long glId, CancellationToken ct)
    {
        if (p is null || p.GitLabProject is null)
            return;

        IReadOnlyList<IssueBasic> rmIssues = await _redmine.GetProjectIssuesBasicAsync(p.RedmineIdentifier, ct);
        IReadOnlyList<IssueBasic> glIssues = await _gitlab.GetProjectIssuesBasicAsync(glId, ct);

        // Existing mappings for this project (avoid duplicates)
        var mappedRm = new HashSet<int>(_db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .Select(m => m.RedmineIssueId));
        var mappedGl = new HashSet<long>(_db.IssueMappings
            .Where(m => m.ProjectSyncId == p.Id)
            .Select(m => m.GitLabIssueId));
        // Get all non duplicated issues from gitlab
        var glByTitle = glIssues
            .Where(i => !string.IsNullOrWhiteSpace(i.Title))
            .GroupBy(i => i.Title.Trim(), System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), System.StringComparer.OrdinalIgnoreCase);

        foreach (var rmi in rmIssues)
        {
            // the issue already exists in mappedRM, continue
            if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) continue;
            var title = rmi.Title?.Trim();
            if (string.IsNullOrEmpty(title)) continue;
            // check to see if that titles exists in gitlab already
            if (!glByTitle.TryGetValue(title, out var cands) || cands.Count != 1) continue;
            var gl = cands[0];
            if (gl.GitLabIid is not long giid || mappedGl.Contains(giid)) continue;

            _db.IssueMappings.Add(new IssueMapping
            {
                ProjectSyncId = p.Id,
                RedmineIssueId = rmId,
                GitLabIssueId = giid,
            });
            mappedRm.Add(rmId);
            mappedGl.Add(giid);
        }

        await _db.SaveChangesAsync(ct);



        foreach (var rmi in rmIssues) // rmIssues already filtered to Feature/Bug
        {
            await CreateGitLabFromRedmineAsync(p, glId, rmi, mappedRm, mappedGl, ct);
        }
        await _db.SaveChangesAsync(ct);

        foreach (var gli in glIssues)
        {
            await CreateRedmineFromGitLabAsync(p, gli, mappedRm, mappedGl, ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task CreateRedmineFromGitLabAsync(ProjectSync p, IssueBasic gli, HashSet<int> mappedRm, HashSet<long> mappedGl, CancellationToken ct)
    {
        if (gli.GitLabIid is not long giid || mappedGl.Contains(giid))
            return;

        // Example: choose tracker based on GitLab label
        var trackerName = gli.Labels?.FirstOrDefault() ?? "Feature";// adjust selection logic
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

        var gitlabUrl = $"{p.GitLabProject.Url}/-/issues/{gli.GitLabIid}";


        int? statusId = null;
        if (string.Equals(gli.Status, "closed", StringComparison.OrdinalIgnoreCase))
        {
            // Closed in GitLab → use Closed in Redmine
            statusId = await _db.StatusesRedmine
                .Where(s => s.Name == "Closed")
                .Select(s => (int?)s.RedmineStatusId)
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            // Opened in GitLab → default to "New"
            statusId = await _db.StatusesRedmine
                .Where(s => s.Name == "New")
                .Select(s => (int?)s.RedmineStatusId)
                .FirstOrDefaultAsync(ct);
        }



        var (rok, newRmId, rmsg) = await _redmine.CreateIssueAsync(
            p.RedmineIdentifier,
            gli.Title,
            gli.Description,
            trackerId,   // now included
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

        _db.IssueMappings.Add(new IssueMapping
        {
            ProjectSyncId = p.Id,
            RedmineIssueId = newRmId,
            GitLabIssueId = giid,
        });

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


        // by default it is open, only when is closed we set it as closed
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

        _db.IssueMappings.Add(new IssueMapping
        {
            ProjectSyncId = p.Id,
            RedmineIssueId = rmId,
            GitLabIssueId = newIid,
        });

        mappedRm.Add(rmId);
        mappedGl.Add(newIid);
    }
    

    
}

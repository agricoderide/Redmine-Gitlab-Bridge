using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;


namespace Bridge.Services;

public sealed class DbSeeder
{
    private readonly RedmineClient _redmine;
    private readonly GitLabClient _gitlab;
    private readonly SyncDbContext _db;
    private readonly ILogger<DbSeeder> _log;

    public DbSeeder(RedmineClient redmine, GitLabClient gitlab, SyncDbContext db, ILogger<DbSeeder> log)
    {
        _redmine = redmine;
        _gitlab = gitlab;
        _db = db;
        _log = log;
    }




    public async Task SeedAsync(CancellationToken ct = default)
    {
        // 1) Apply pending EF Core migrations
        await _db.Database.MigrateAsync(ct);

        // Create Users table, i do not care about membership

        #region RedmineAndAssociatedGitlabProjects 
        // And also fill the gitlab table
        ////////////////////////// 2) Fill the Redmine Table
        IReadOnlyList<ProjectLink> listProjects =
            await _redmine.GetProjectsWithGitLabLinksAsync(_redmine._opt.GitlabCustomField, ct);

        // Upsert projects + GitLab link metadata
        foreach (var goodProjWithGitlab in listProjects)
        {
            ct.ThrowIfCancellationRequested();

            var proj = await _db.Projects
                .Include(p => p.GitLabProject)
                .SingleOrDefaultAsync(p => p.RedmineProjectId == goodProjWithGitlab.RedmineProjectId, ct);

            if (proj is null)
            {
                proj = new ProjectSync
                {
                    RedmineProjectId = goodProjWithGitlab.RedmineProjectId,
                    RedmineIdentifier = goodProjWithGitlab.RedmineIdentifier,
                };
                _db.Projects.Add(proj);
            }
            else
            {
                // Refresh local metadata
                proj.RedmineIdentifier = goodProjWithGitlab.RedmineIdentifier;
            }



            ///////////////////////////////////////////////////////// 3) Fill the gitlab table
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
            ////////////////////////////////////////////////////////////////////////////////////////////////////
            await _db.SaveChangesAsync(ct);
        }
        #endregion




        var projects = await _db.Projects
            .Include(p => p.GitLabProject)
            .ToListAsync(ct);

        foreach (var p in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (p.GitLabProject?.GitLabProjectId is not long glId)
                continue; // need concrete GitLab project id

            var rmKey = p.RedmineProjectId.ToString();



            #region Users
            try
            {
                var gitlabUsers = await _gitlab.GetGitLabProjectMembersAsync((int)p.GitLabProject.GitLabProjectId);
                var redmineUsers = await _redmine.GetRedmineMembersAsync(p.Id);

                var seen = new HashSet<(int rmId, int glId)>();
                var toAdd = new List<User>();

                foreach (var gl in gitlabUsers)
                {
                    var key = ExtractSearchKey(gl.Username);

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
            #endregion




            #region IssuesSyncing

            IReadOnlyList<IssueBasic> rmIssues = await _redmine.GetProjectIssuesBasicAsync(rmKey, ct);
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
                    LastSyncedUtc = System.DateTimeOffset.UtcNow
                });
                mappedRm.Add(rmId);
                mappedGl.Add(giid);
            }

            await _db.SaveChangesAsync(ct);



            // Keep filling issues for issues not with title match
            foreach (var rmi in rmIssues) // rmIssues already filtered to Feature/Bug
            {
                if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) continue;


                var (ok, newIid, msg) = await _gitlab.CreateIssueAsync(
                    glId,
                    rmi.Title,
                    rmi.Description,
                    ct
                );
                if (!ok)
                {
                    _log.LogWarning("GitLab create issue failed for project {GlProjectId}: {Msg}", glId, msg);
                    continue;
                }

                _db.IssueMappings.Add(new IssueMapping
                {
                    ProjectSyncId = p.Id,
                    RedmineIssueId = rmId,
                    GitLabIssueId = newIid,
                    LastSyncedUtc = System.DateTimeOffset.UtcNow
                });

                mappedRm.Add(rmId);
                mappedGl.Add(newIid);
            }

            await _db.SaveChangesAsync(ct);


            foreach (var gli in glIssues)
            {
                if (gli.GitLabIid is not long giid || mappedGl.Contains(giid)) continue;

                var (rok, newRmId, rmsg) = await _redmine.CreateIssueAsync(
                    rmKey,
                    gli.Title,
                    gli.Description,
                    ct
                );
                if (!rok)
                {
                    _log.LogWarning("Redmine create issue failed for project {RmKey}: {Msg}", rmKey, rmsg);
                    continue;
                }

                _db.IssueMappings.Add(new IssueMapping
                {
                    ProjectSyncId = p.Id,
                    RedmineIssueId = newRmId,
                    GitLabIssueId = giid,
                    LastSyncedUtc = System.DateTimeOffset.UtcNow
                });
                mappedGl.Add(giid);
                mappedRm.Add(newRmId);
            }

            await _db.SaveChangesAsync(ct);

            #endregion
        }

    }

    static string ExtractSearchKey(string username)
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


}
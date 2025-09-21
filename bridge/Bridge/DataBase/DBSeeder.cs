// Bridge/Services/DbSeeder.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bridge.Services;

/// <summary>
/// Seeds projects from Redmine and synchronizes issues with GitLab.
/// 1) Migrate DB
/// 2) Upsert ProjectSync + GitLabProject (from Redmine CF)
/// 3) Resolve GitLab project ID (if path present)
/// 4) Pair issues by exact (trimmed, case-insensitive) title
/// 5) Backfill remaining issues (directions configurable)
/// Plus:
/// - Only sync Redmine trackers: Feature/Bug
/// - For GL → RM, choose Redmine tracker based on GitLab labels ("feature"/"bug")
/// - Upsert Redmine project trackers into local Trackers table
/// </summary>
public sealed class DbSeeder
{
    // Toggle backfill directions to be safer in production if needed.
    private const bool BackfillRedmineToGitLab = true;
    private const bool BackfillGitLabToRedmine = true;

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
                    Name = goodProjWithGitlab.Name
                };
                _db.Projects.Add(proj);
            }
            else
            {
                // Refresh local metadata
                proj.RedmineIdentifier = goodProjWithGitlab.RedmineIdentifier;
                proj.Name = goodProjWithGitlab.Name;
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





        // 5) Pair + 6) Backfill issues between Redmine and GitLab
        var projects = await _db.Projects
            .Include(p => p.GitLabProject)
            .ToListAsync(ct);

        foreach (var p in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (p.GitLabProject?.GitLabProjectId is not long glId)
                continue; // need concrete GitLab project id

            // Prefer identifier, else numeric Redmine id
            var rmKey = string.IsNullOrWhiteSpace(p.RedmineIdentifier)
                ? p.RedmineProjectId.ToString()
                : p.RedmineIdentifier;


            #region User
            var rmMembers = await _redmine.GetProjectMembersAsync(rmKey, ct);
            foreach (var (id, name, login, email) in rmMembers)
            {
                var u = await _db.Users.SingleOrDefaultAsync(x => x.RedmineUserId == id, ct);
                if (u is null)
                {
                    _db.Users.Add(new User
                    {
                        RedmineUserId = id,
                        DisplayName = name,
                        RedmineLogin = login,
                        Email = email
                    });
                }
                else
                {
                    if (!StringComparer.Ordinal.Equals(u.DisplayName, name)) u.DisplayName = name;
                    if (!StringComparer.Ordinal.Equals(u.RedmineLogin, login)) u.RedmineLogin = login;
                    if (!StringComparer.Ordinal.Equals(u.Email, email)) u.Email = email;
                }
            }
            await _db.SaveChangesAsync(ct);

            // link memberships to the project (source=Redmine)
            foreach (var (id, _, _, _) in rmMembers)
            {
                var uid = await _db.Users.Where(x => x.RedmineUserId == id).Select(x => x.Id).FirstAsync(ct);
                var exists = await _db.ProjectMemberships.AnyAsync(m =>
                    m.ProjectSyncId == p.Id && m.UserId == uid && m.Source == "Redmine", ct);
                if (!exists)
                    _db.ProjectMemberships.Add(new ProjectMembership { ProjectSyncId = p.Id, UserId = uid, Source = "Redmine" });
            }
            await _db.SaveChangesAsync(ct);

            // GitLab members
            var glMembers = await _gitlab.GetProjectMembersAsync(glId, ct);
            foreach (var (id, name, username, email) in glMembers)
            {
                var u = await _db.Users.SingleOrDefaultAsync(x => x.GitLabUserId == id, ct);
                if (u is null)
                {
                    _db.Users.Add(new User
                    {
                        GitLabUserId = id,
                        DisplayName = name,
                        GitLabUsername = username,
                        Email = email
                    });
                }
                else
                {
                    if (!StringComparer.Ordinal.Equals(u.DisplayName, name)) u.DisplayName = name;
                    if (!StringComparer.Ordinal.Equals(u.GitLabUsername, username)) u.GitLabUsername = username;
                    if (!StringComparer.Ordinal.Equals(u.Email, email)) u.Email = email;
                }
            }
            await _db.SaveChangesAsync(ct);

            // link memberships to the project (source=GitLab)
            foreach (var (id, _, _, _) in glMembers)
            {
                var uid = await _db.Users.Where(x => x.GitLabUserId == id).Select(x => x.Id).FirstAsync(ct);
                var exists = await _db.ProjectMemberships.AnyAsync(m =>
                    m.ProjectSyncId == p.Id && m.UserId == uid && m.Source == "GitLab", ct);
                if (!exists)
                    _db.ProjectMemberships.Add(new ProjectMembership { ProjectSyncId = p.Id, UserId = uid, Source = "GitLab" });
            }
            await _db.SaveChangesAsync(ct);
            #endregion

            #region Trackers
            var trackers = await _redmine.GetProjectTrackersAsync(rmKey, ct);
            foreach (var (tid, name) in trackers)
            {
                var existing = await _db.Trackers.SingleOrDefaultAsync(t => t.RedmineTrackerId == tid, ct);
                if (existing is null)
                {
                    _db.Trackers.Add(new Tracker { RedmineTrackerId = tid, Name = name });
                }
                else if (!StringComparer.Ordinal.Equals(existing.Name, name))
                {
                    existing.Name = name;
                }
            }
            await _db.SaveChangesAsync(ct);
            #endregion


            #region IssuesSyncing
            var featureId = await _db.Trackers
                .Where(t => t.Name == "Feature")
                .Select(t => (int?)t.RedmineTrackerId)
                .FirstOrDefaultAsync(ct);

            var bugId = await _db.Trackers
                .Where(t => t.Name == "Bug")
                .Select(t => (int?)t.RedmineTrackerId)
                .FirstOrDefaultAsync(ct);

            // Fetch basic issues (NOTE: consider pagination if >300 items expected)
            var rmIssues = await _redmine.GetProjectIssuesBasicAsync(rmKey, ct);
            var glIssues = await _gitlab.GetProjectIssuesBasicAsync(glId, ct);

            // Filter Redmine issues to only Feature or Bug
            rmIssues = rmIssues
                .Where(i => string.Equals(i.TrackerName, "Feature", System.StringComparison.OrdinalIgnoreCase)
                         || string.Equals(i.TrackerName, "Bug", System.StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Existing mappings for this project (avoid duplicates)
            var mappedRm = new HashSet<int>(_db.IssueMappings
                .Where(m => m.ProjectSyncId == p.Id)
                .Select(m => m.RedmineIssueId));

            var mappedGl = new HashSet<long>(_db.IssueMappings
                .Where(m => m.ProjectSyncId == p.Id)
                .Select(m => m.GitLabIssueId));

            // Pair by exact (trimmed, case-insensitive) title — unique matches only
            var glByTitle = glIssues
                .Where(i => !string.IsNullOrWhiteSpace(i.Title))
                .GroupBy(i => i.Title.Trim(), System.StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), System.StringComparer.OrdinalIgnoreCase);

            foreach (var rmi in rmIssues)
            {
                if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) continue;

                var title = rmi.Title?.Trim();
                if (string.IsNullOrEmpty(title)) continue;

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
            if (BackfillRedmineToGitLab)
            {
                foreach (var rmi in rmIssues) // rmIssues already filtered to Feature/Bug
                {
                    if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) continue;

                    // extra guard in case filter changes later
                    var trackerName = (rmi.TrackerName ?? "").Trim();
                    if (!trackerName.Equals("feature", StringComparison.OrdinalIgnoreCase) &&
                        !trackerName.Equals("bug", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // set a GitLab label that mirrors the tracker
                    var glLabels = trackerName.Equals("bug", StringComparison.OrdinalIgnoreCase) ? "bug" : "feature";

                    var (ok, newIid, msg) = await _gitlab.CreateIssueAsync(
                        glId,
                        rmi.Title,
                        rmi.Description,
                        labelsCsv: glLabels,  // <— important: add label on GL side
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
            }
            // 6) Backfill GL → RM for unmapped issues
            // Create only if GitLab labels indicate "bug" or "feature",
            // and use the corresponding Redmine tracker id.
            if (BackfillGitLabToRedmine)
            {
                foreach (var gli in glIssues)
                {
                    if (gli.GitLabIid is not long giid || mappedGl.Contains(giid)) continue;

                    // Decide tracker from labels
                    int? trackerIdToUse = null;
                    if (gli.Labels is { Count: > 0 })
                    {
                        bool isBug = gli.Labels.Any(l => string.Equals(l, "bug", System.StringComparison.OrdinalIgnoreCase));
                        bool isFeature = gli.Labels.Any(l => string.Equals(l, "feature", System.StringComparison.OrdinalIgnoreCase));
                        if (isBug && bugId != null) trackerIdToUse = bugId;
                        else if (isFeature && featureId != null) trackerIdToUse = featureId;
                    }

                    // If no suitable label → skip creating in Redmine
                    if (trackerIdToUse is null) continue;

                    var (rok, newRmId, rmsg) = await _redmine.CreateIssueAsync(
                        rmKey,
                        gli.Title,
                        gli.Description,
                        ct,
                        trackerId: trackerIdToUse.Value
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
            }
            #endregion
        }

    }
}
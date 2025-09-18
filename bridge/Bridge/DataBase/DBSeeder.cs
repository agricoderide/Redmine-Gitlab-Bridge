// Seeds and synchronizes initial data between Redmine and GitLab.
// Steps:
// 1) Ensure DB is migrated
// 2) Import Redmine projects that have a GitLab custom field link
// 3) Upsert local ProjectSync records and attach GitLabProject metadata
// 4) Optionally resolve numeric GitLab project IDs (if token configured)
// 5) Associate existing Redmine/GitLab issues by normalized title
// 6) Backfill missing issues in both systems where safe
using Bridge.Contracts;
using Bridge.Data;
using Bridge.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bridge.Services;

public sealed class DbSeeder
{
    // External clients and infrastructure
    private readonly RedmineClient _redmine;
    private readonly GitLabClient _gitlab;
    private readonly SyncDbContext _db;
    private readonly ILogger<DbSeeder> _log;
    private readonly GitLabOptions _gitlabOpts;

    // Name of Redmine custom field that stores the GitLab repo link
    private const string CfName = "Gitlab Repo";

    public DbSeeder(RedmineClient redmine, GitLabClient gitlab, SyncDbContext db,
                    IOptions<GitLabOptions> gitlabOpts, ILogger<DbSeeder> log)
    {
        _redmine = redmine;
        _gitlab = gitlab;
        _db = db;
        _gitlabOpts = gitlabOpts.Value;
        _log = log;
    }

    // Entry point: runs the seeding + association/backfill workflow
    public async Task SeedAsync(CancellationToken ct = default)
    {
        // 1) Apply pending EF Core migrations
        await _db.Database.MigrateAsync(ct);

        // 2) Fetch Redmine projects which have a GitLab link in the custom field
        var links = await _redmine.GetProjectsWithGitLabLinksAsync(CfName, ct);

        // Stats for logging
        int considered = 0, upserted = 0, linked = 0, skippedNoLink = 0;
        
        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            considered++;

            // Skip projects without a GitLab URL in Redmine
            if (link.GitLabUrl is null)
            {
                skippedNoLink++;
                continue;
            }

            // Find or create the local ProjectSync record for this Redmine project
            var proj = await _db.Projects
                .Include(p => p.GitLabProject)
                .SingleOrDefaultAsync(p => p.RedmineProjectId == link.RedmineProjectId, ct);

            if (proj is null)
            {
                proj = new ProjectSync
                {
                    RedmineProjectId = link.RedmineProjectId,
                    RedmineIdentifier = link.RedmineIdentifier,
                    Name = link.Name
                };
                _db.Projects.Add(proj);
                upserted++;
            }
            else
            {
                // Keep local metadata fresh
                proj.RedmineIdentifier = link.RedmineIdentifier;
                proj.Name = link.Name;
            }

            // 3) Ensure GitLab repository link metadata is present and current
            if (proj.GitLabProject is null)
            {
                proj.GitLabProject = new GitLabProject
                {
                    Url = link.GitLabUrl!,
                    PathWithNamespace = link.GitLabPathWithNs!
                };
                linked++;
            }
            else
            {
                if (!StringComparer.Ordinal.Equals(proj.GitLabProject.Url, link.GitLabUrl) ||
                    !StringComparer.Ordinal.Equals(proj.GitLabProject.PathWithNamespace, link.GitLabPathWithNs))
                {
                    proj.GitLabProject.Url = link.GitLabUrl!;
                    proj.GitLabProject.PathWithNamespace = link.GitLabPathWithNs!;
                    linked++;
                }
            }

            // 4) Optionally resolve the numeric GitLab project ID from its path
            //    (requires PrivateToken to query GitLab API)
            if (!string.IsNullOrWhiteSpace(_gitlabOpts.PrivateToken) &&
                proj.GitLabProject?.GitLabProjectId is null &&
                proj.GitLabProject?.PathWithNamespace is string path)
            {
                var (ok, id, msg) = await _gitlab.EnsureProjectIdAsync(path, ct);
                if (ok) proj.GitLabProject!.GitLabProjectId = id;
                else _log.LogWarning("Resolve GitLab id failed for {Path}: {Msg}", path, msg);
            }

            // Persist after each project to keep state consistent
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation("Projects seeded. Considered={C}, Upserted={U}, LinkedOrUpdatedRepos={L}, SkippedNoGitLabLink={S}",
            considered, upserted, linked, skippedNoLink);

        // If there is no GitLab token, we cannot list/create issues → stop here
        if (string.IsNullOrWhiteSpace(_gitlabOpts.PrivateToken))
        {
            _log.LogInformation("GitLab token not configured; skipping issue association.");
            return;
        }

        // 5) Issue association & 6) backfill between Redmine and GitLab
        var projects = await _db.Projects.Include(p => p.GitLabProject).ToListAsync(ct);

        int examined = 0, paired = 0, ambig = 0, createdGL = 0, createdRM = 0;
        foreach (var p in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (p.GitLabProject?.GitLabProjectId is not long glId) continue;
            examined++;

            // Determine Redmine project key (identifier preferred, else numeric id)
            var rmKey = string.IsNullOrWhiteSpace(p.RedmineIdentifier)
                ? p.RedmineProjectId.ToString()
                : p.RedmineIdentifier;

            // Fetch basic issue lists from both systems
            var rmIssues = await _redmine.GetProjectIssuesBasicAsync(rmKey, ct);
            var glIssues = await _gitlab.GetProjectIssuesBasicAsync(glId, ct);

            // Local maps of already-known pairings to avoid duplicates
            var mappedRm = new HashSet<int>(_db.IssueMappings
                .Where(m => m.ProjectSyncId == p.Id)
                .Select(m => m.RedmineIssueId));
            var mappedGl = new HashSet<long>(_db.IssueMappings
                .Where(m => m.ProjectSyncId == p.Id)
                .Select(m => m.GitLabIssueId));

            // Build an index of GitLab issues by normalized title for matching
            static string Key(string s) => string.IsNullOrWhiteSpace(s)
                ? string.Empty
                : Normalize(s);
            var glByTitle = glIssues
                .GroupBy(i => Key(i.Title))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            // 5) Pair Redmine → GitLab issues by unique normalized title
            foreach (var rmi in rmIssues)
            {
                if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) continue;
                var k = Key(rmi.Title);
                if (k == "" || !glByTitle.TryGetValue(k, out var cands) || cands.Count == 0) continue;
                if (cands.Count > 1) { ambig++; continue; }

                var gl = cands[0];
                if (gl.GitLabIid is not long giid || mappedGl.Contains(giid)) continue;

                _db.IssueMappings.Add(new IssueMapping
                {
                    ProjectSyncId = p.Id,
                    RedmineIssueId = rmId,
                    GitLabIssueId = giid,
                    LastSyncedUtc = DateTimeOffset.UtcNow
                });
                mappedRm.Add(rmId); mappedGl.Add(giid); paired++;
            }
            await _db.SaveChangesAsync(ct);

            // 6) Backfill remaining Redmine issues into GitLab where no mapping exists
            foreach (var rmi in rmIssues)
            {
                if (rmi.RedmineId is not int rmId || mappedRm.Contains(rmId)) continue;

                var (ok, newIid, msg) = await _gitlab.CreateIssueBasicAsync(glId, rmi, ct);
                if (!ok) { _log.LogWarning("GitLab create issue failed: {Msg}", msg); continue; }

                _db.IssueMappings.Add(new IssueMapping
                {
                    ProjectSyncId = p.Id,
                    RedmineIssueId = rmId,
                    GitLabIssueId = newIid,
                    LastSyncedUtc = DateTimeOffset.UtcNow
                });
                mappedRm.Add(rmId); mappedGl.Add(newIid); createdGL++;
            }
            await _db.SaveChangesAsync(ct);

            // 6) Backfill remaining GitLab issues into Redmine where no mapping exists
            foreach (var gli in glIssues)
            {
                if (gli.GitLabIid is not long giid || mappedGl.Contains(giid)) continue;

                var (rok, newRmId, rmsg) = await _redmine.CreateIssueBasicAsync(rmKey, gli, ct);
                if (!rok) { _log.LogWarning("Redmine create issue failed: {Msg}", rmsg); continue; }

                _db.IssueMappings.Add(new IssueMapping
                {
                    ProjectSyncId = p.Id,
                    RedmineIssueId = newRmId,
                    GitLabIssueId = giid,
                    LastSyncedUtc = DateTimeOffset.UtcNow
                });
                mappedGl.Add(giid); mappedRm.Add(newRmId); createdRM++;
            }
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation("Issue association complete. ProjectsExamined={E}, PairedByTitle={P}, AmbiguousSkipped={A}, CreatedInGitLab={GL}, CreatedInRedmine={RM}",
            examined, paired, ambig, createdGL, createdRM);

        static string Normalize(string s)
        {
            // Simple normalization for title comparison: trim, lowercase,
            // collapse whitespace, and strip newlines to improve matching.
            s = s.Trim().ToLowerInvariant().Replace("\r", "").Replace("\n", " ");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }
    }
}

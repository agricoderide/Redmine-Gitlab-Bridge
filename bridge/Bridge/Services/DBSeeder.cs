// Bridge/Services/DbSeeder.cs
using Bridge.Data;
using Bridge.Infrastructure.Options;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bridge.Services;

public sealed class DbSeeder
{
    private readonly RedmineClient _redmine;
    private readonly GitLabClient _gitlab;
    private readonly SyncDbContext _db;
    private readonly ILogger<DbSeeder> _log;
    private readonly IOptions<GitLabOptions> _gitlabOpts;

    private const string CfName = "Gitlab Repo"; // change if your CF has a different name

    public DbSeeder(RedmineClient redmine, GitLabClient gitlab, SyncDbContext db,
                    IOptions<GitLabOptions> gitlabOpts, ILogger<DbSeeder> log)
    {
        _redmine = redmine;
        _gitlab = gitlab;
        _db = db;
        _gitlabOpts = gitlabOpts;
        _log = log;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await _db.Database.MigrateAsync(ct);

        _log.LogInformation("Seeding: loading Redmine projects list…");
        var list = await _redmine.GetProjectsAsync(ct);

        int considered = 0, upsertedProjects = 0, setGitLabLink = 0, skippedNoLink = 0;

        foreach (var p in list)
        {
            ct.ThrowIfCancellationRequested();
            considered++;

            int redmineId = p.GetProperty("id").GetInt32();
            string identifier = p.GetProperty("identifier").GetString() ?? redmineId.ToString();
            string name = p.TryGetProperty("name", out var nameProp)
                ? (nameProp.GetString() ?? identifier)
                : identifier;

            // Extract GitLab link directly from list item (custom_fields included). Skip if missing.
            string? url = RedmineParsing.ExtractSingleGitLabUrlFromProject(p, CfName);
            if (string.IsNullOrWhiteSpace(url))
            {
                skippedNoLink++;
                continue;
            }

            // Upsert only projects that have a GitLab association
            var project = await _db.Projects
                .Include(x => x.GitLabProject)
                .SingleOrDefaultAsync(x => x.RedmineProjectId == redmineId, ct);

            if (project is null)
            {
                project = new ProjectSync
                {
                    RedmineProjectId = redmineId,
                    RedmineIdentifier = identifier,
                    Name = name,
                    LastSyncUtc = null
                };
                _db.Projects.Add(project);
                upsertedProjects++;
            }
            else
            {
                project.RedmineIdentifier = identifier;
                project.Name = name;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                string path = RedmineParsing.PathFromUrl(url);
                // Ensure there is exactly one GitLabProject record (create/update)
                if (project.GitLabProject is null)
                {
                    project.GitLabProject = new GitLabProject
                    {
                        Url = url!,
                        PathWithNamespace = path,
                        // GitLabProjectId stays null until resolved
                    };
                    setGitLabLink++;
                }
                else
                {
                    if (project.GitLabProject.Url != url || project.GitLabProject.PathWithNamespace != path)
                    {
                        project.GitLabProject.Url = url!;
                        project.GitLabProject.PathWithNamespace = path;
                        setGitLabLink++;
                    }
                }

                // Optionally resolve numeric GitLab project ID if token configured
                if (project.GitLabProject.GitLabProjectId is null &&
                    !string.IsNullOrWhiteSpace(_gitlabOpts.Value.PrivateToken))
                {
                    var (ok, id, msg) = await _gitlab.ResolveProjectIdAsync(path, ct);
                    if (ok) project.GitLabProject.GitLabProjectId = id;
                    else _log.LogWarning("Resolve GitLab id failed for {Path}: {Msg}", path, msg);
                }
            }
            else
            {
                // No GitLab link – leave as null; optionally detach existing link
                // If you want to clear stale links when field is removed, uncomment:
                // if (project.GitLabProject is not null) _db.Remove(project.GitLabProject);
            }
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Projects seeded. Considered={Considered}, UpsertedProjects={Projects}, LinkedOrUpdatedRepos={Repos}, SkippedNoGitLabLink={Skipped}",
            considered, upsertedProjects, setGitLabLink, skippedNoLink);

        // Optionally associate issues between Redmine and GitLab for projects that have a GitLab project id
        await AssociateIssuesAsync(ct);
    }

    private static string NormalizeTitle(string s) => (s ?? "").Trim().ToLowerInvariant().Replace("\r", "").Replace("\n", " ").Replace("  ", " ");

    private async Task AssociateIssuesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_gitlabOpts.Value.PrivateToken))
        {
            _log.LogInformation("GitLab token not configured; skipping issue association.");
            return;
        }

        var projects = await _db.Projects
            .Include(p => p.GitLabProject)
            .ToListAsync(ct);

        int examinedProjects = 0, newLinks = 0, skippedAmbiguous = 0;

        foreach (var p in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (p.GitLabProject?.GitLabProjectId is not long glProjectId)
                continue; // need a concrete GitLab project id

            examinedProjects++;

            // Fetch Redmine issues for the project identifier; default to id if identifier empty
            var redmineKey = string.IsNullOrWhiteSpace(p.RedmineIdentifier) ? p.RedmineProjectId.ToString() : p.RedmineIdentifier;
            IReadOnlyList<JsonElement> rmIssues;
            try
            {
                rmIssues = await _redmine.GetProjectIssuesAsync(redmineKey, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load Redmine issues for {Project}", redmineKey);
                continue;
            }

            IReadOnlyList<JsonElement> glIssues;
            try
            {
                glIssues = await _gitlab.GetProjectIssuesAsync(glProjectId, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load GitLab issues for project id {ProjectId}", glProjectId);
                continue;
            }

            // Build lookups based on normalized title/subject
            var glByTitle = glIssues
                .GroupBy(i => NormalizeTitle(i.GetProperty("title").GetString() ?? ""))
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var rmi in rmIssues)
            {
                ct.ThrowIfCancellationRequested();
                int rmId = rmi.GetProperty("id").GetInt32();
                string rmSubject = rmi.TryGetProperty("subject", out var s) ? (s.GetString() ?? "") : "";

                // skip if already mapped
                bool exists = await _db.IssueMappings.AnyAsync(x => x.RedmineIssueId == rmId, ct);
                if (exists) continue;

                var key = NormalizeTitle(rmSubject);
                if (string.IsNullOrWhiteSpace(key)) continue;

                // Okay no match by title
                // title is not really important, 
                // i just need a way to 
                if (!glByTitle.TryGetValue(key, out var candidates) || candidates.Count == 0)
                    continue; // no match by title.

                if (candidates.Count > 1)
                {
                    skippedAmbiguous++;
                    continue; // ambiguous title; skip
                }

                var gl = candidates[0];
                long glId = gl.GetProperty("id").GetInt64();

                // ensure GitLab id not already linked elsewhere
                bool glTaken = await _db.IssueMappings.AnyAsync(x => x.GitLabIssueId == glId, ct);
                if (glTaken) continue;

                _db.IssueMappings.Add(new IssueMapping
                {
                    RedmineIssueId = rmId,
                    GitLabIssueId = glId,
                    ProjectSyncId = p.Id,
                    LastSyncedUtc = DateTimeOffset.UtcNow,
                    Fingerprint = null
                });
                newLinks++;
            }

            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation("Issue association complete. ProjectsExamined={Projects}, NewLinks={NewLinks}, AmbiguousSkipped={Ambiguous}",
            examinedProjects, newLinks, skippedAmbiguous);
    }
}

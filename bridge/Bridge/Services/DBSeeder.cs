// Bridge/Services/DbSeeder.cs
using Bridge.Data;
using Bridge.Infrastructure.Options;
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

    private const string CfName = "GitLab Projects"; // change if your CF has a different name

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
        var list = await _redmine.GetProjectsListAsync(ct);

        int considered = 0, upsertedProjects = 0, setGitLabLink = 0;

        foreach (var p in list)
        {
            ct.ThrowIfCancellationRequested();
            considered++;

            int redmineId = p.GetProperty("id").GetInt32();
            string identifier = p.GetProperty("identifier").GetString() ?? redmineId.ToString();
            string name = p.TryGetProperty("name", out var nameProp)
                ? (nameProp.GetString() ?? identifier)
                : identifier;

            // Upsert ProjectSync regardless of GitLab association
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

            // Try to load details to resolve GitLab association
            var details = await _redmine.GetProjectDetailsAsync(identifier, ct);
            string? url = details is null
                ? null
                : RedmineParsing.ExtractSingleGitLabUrlFromProject(details.Value, CfName);

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
        _log.LogInformation("Seeding done. Considered={Considered}, UpsertedProjects={Projects}, LinkedOrUpdatedRepos={Repos}",
            considered, upsertedProjects, setGitLabLink);
    }
}

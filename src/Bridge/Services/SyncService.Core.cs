using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
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

    // Sync every poll tick
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _db.Database.MigrateAsync(ct);
        await _redmine.SyncGlobalTrackersAsync(ct);
        await _redmine.SyncGlobalStatusesAsync(ct);

        await GetRedmine_GitlabProjects(ct);

        var projects = await _db.Projects.AsNoTracking().Include(p => p.GitLabProject).ToListAsync(ct);
        foreach (var p in projects)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (p.GitLabProject?.GitLabProjectId is not long glId) continue;

                await SyncMembersAsync(p, ct);
                await SyncIssuesAsync(p, glId, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sync failed per project {Pid}", p.Id);
            }
        }
    }
}

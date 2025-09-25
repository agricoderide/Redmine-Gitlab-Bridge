using Bridge.Contracts;
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
                await SyncIssuesAsync(p, glId, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sync failed per project {Pid}", p.Id);
            }
        }
    }
}

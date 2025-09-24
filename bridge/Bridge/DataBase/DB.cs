using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bridge.Data;

public sealed class SyncDbContext : DbContext
{
    public DbSet<ProjectSync> Projects => Set<ProjectSync>();
    public DbSet<GitLabProject> GitLabProjects => Set<GitLabProject>();
    public DbSet<IssueMapping> IssueMappings => Set<IssueMapping>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TrackerRedmine> TrackersRedmine => Set<TrackerRedmine>();
    public DbSet<StatusRedmine> StatusesRedmine => Set<StatusRedmine>();
    public SyncDbContext(DbContextOptions<SyncDbContext> options) : base(options) { }
}



[Index(nameof(RedmineProjectId), IsUnique = true)]
public sealed class ProjectSync
{
    [Key] public int Id { get; set; }
    public int RedmineProjectId { get; set; }
    [Required] public string RedmineIdentifier { get; set; } = "";
    public DateTimeOffset? LastSyncUtc { get; set; }
    public GitLabProject? GitLabProject { get; set; }
}



public sealed class GitLabProject
{
    [Key] public int Id { get; set; }
    public long? GitLabProjectId { get; set; }
    [Required] public string PathWithNamespace { get; set; } = "";
    [Required] public string Url { get; set; } = "";
    [ForeignKey(nameof(ProjectSync))] public int ProjectSyncId { get; set; }
    public ProjectSync ProjectSync { get; set; } = null!;
}



[Index(nameof(RedmineIssueId), IsUnique = true)]
[Index(nameof(GitLabIssueId), IsUnique = true)]
public sealed class IssueMapping
{
    [Key] public int Id { get; set; }
    public int RedmineIssueId { get; set; }
    public long GitLabIssueId { get; set; }
    [ForeignKey(nameof(ProjectSync))] public int ProjectSyncId { get; set; }
    public ProjectSync ProjectSync { get; set; } = null!;

    // NEW: single canonical snapshot (IssueBasic serialized to JSON)
    public string? CanonicalSnapshotJson { get; set; }

    // Optional: for webhook idempotency (GitLab Issues Hook)
    public string? LastGitLabEventUuid { get; set; }
}



[Index(nameof(RedmineUserId), IsUnique = true)]
[Index(nameof(GitLabUserId), IsUnique = true)]
public sealed class User
{
    [Key] public int Id { get; set; }

    public int? RedmineUserId { get; set; }   // RM numeric id
    public int? GitLabUserId { get; set; }   // GL numeric id

    public string? Username { get; set; }

}

[Index(nameof(RedmineTrackerId), IsUnique = true)]
[Index(nameof(Name), IsUnique = true)]
public sealed class TrackerRedmine
{
    [Key] public int Id { get; set; }                // PK in your own DB
    [Required] public int RedmineTrackerId { get; set; }  // Redmine’s tracker id
    [Required] public string Name { get; set; } = null!;
}


[Index(nameof(RedmineStatusId), IsUnique = true)]
[Index(nameof(Name), IsUnique = true)]
public sealed class StatusRedmine
{
    [Key] public int Id { get; set; }                 // PK
    [Required] public int RedmineStatusId { get; set; }    // numeric id from Redmine
    [Required] public string Name { get; set; } = "";      // e.g. "New", "In Progress", "Closed"
}
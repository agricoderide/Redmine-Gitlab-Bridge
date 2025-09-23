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
    public DateTimeOffset LastSyncedUtc { get; set; }
    public string? Fingerprint { get; set; }
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

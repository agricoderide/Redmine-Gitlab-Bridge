using Bridge.Contracts;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Deterministic JSON for IssueBasic
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        var converter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IssueBasic?, string?>(
            v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, jsonOptions),
            v => string.IsNullOrWhiteSpace(v) ? null : System.Text.Json.JsonSerializer.Deserialize<IssueBasic>(v!, jsonOptions)
        );

        modelBuilder.Entity<IssueMapping>()
            .Property(m => m.CanonicalSnapshot)
            .HasConversion(converter);

        base.OnModelCreating(modelBuilder);
    }
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
    public IssueBasic? CanonicalSnapshot { get; set; }
    public string? LastGitLabEventUuid { get; set; }
}



[Index(nameof(RedmineUserId), IsUnique = true)]
[Index(nameof(GitLabUserId), IsUnique = true)]
public sealed class User
{
    [Key] public int Id { get; set; }

    public int? RedmineUserId { get; set; }  
    public int? GitLabUserId { get; set; }

    public string? Username { get; set; }

}

[Index(nameof(RedmineTrackerId), IsUnique = true)]
[Index(nameof(Name), IsUnique = true)]
public sealed class TrackerRedmine
{
    [Key] public int Id { get; set; }
    [Required] public int RedmineTrackerId { get; set; }
    [Required] public string Name { get; set; } = null!;
}


[Index(nameof(RedmineStatusId), IsUnique = true)]
[Index(nameof(Name), IsUnique = true)]
public sealed class StatusRedmine
{
    [Key] public int Id { get; set; }
    [Required] public int RedmineStatusId { get; set; }
    [Required] public string Name { get; set; } = "";
}
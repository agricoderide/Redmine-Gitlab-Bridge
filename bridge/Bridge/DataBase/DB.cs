// Bridge/Data/SyncDbContext.cs
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bridge.Data;

public sealed class SyncDbContext : DbContext
{
    public DbSet<ProjectSync> Projects => Set<ProjectSync>();
    public DbSet<GitLabProject> GitLabProjects => Set<GitLabProject>();
    public DbSet<IssueMapping> IssueMappings => Set<IssueMapping>();

    public SyncDbContext(DbContextOptions<SyncDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProjectSync>()
            .HasIndex(x => x.RedmineProjectId).IsUnique();

        b.Entity<ProjectSync>()
            .HasOne(p => p.GitLabProject)
            .WithOne(g => g.ProjectSync)
            .HasForeignKey<GitLabProject>(g => g.ProjectSyncId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<IssueMapping>().HasIndex(i => i.RedmineIssueId).IsUnique();
        b.Entity<IssueMapping>().HasIndex(i => i.GitLabIssueId).IsUnique();
    }

    // optional fallback for EF CLI if you drop the factory
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite("Data Source=bridge.db");
        }
    }
}


public sealed class ProjectSync
{
    [Key] public int Id { get; set; }

    public int RedmineProjectId { get; set; }
    public string RedmineIdentifier { get; set; } = "";
    public string Name { get; set; } = "";

    public DateTimeOffset? LastSyncUtc { get; set; }

    // 1:1 navigation
    public GitLabProject? GitLabProject { get; set; }
}

public sealed class GitLabProject
{
    [Key] public int Id { get; set; }

    // May be unknown at seed time → nullable
    public long? GitLabProjectId { get; set; }

    // mygroup/myrepo (no leading slash)
    public string PathWithNamespace { get; set; } = "";

    // Full URL (e.g., https://gitlab.local/mygroup/myrepo)
    public string Url { get; set; } = "";

    // FK for the 1:1
    public int ProjectSyncId { get; set; }
    public ProjectSync ProjectSync { get; set; } = null!;
}

public sealed class IssueMapping
{
    [Key] public int Id { get; set; }

    public int RedmineIssueId { get; set; }
    public long GitLabIssueId { get; set; }

    public int ProjectSyncId { get; set; }
    public ProjectSync ProjectSync { get; set; } = null!;

    public DateTimeOffset LastSyncedUtc { get; set; }
    public string? Fingerprint { get; set; }
}

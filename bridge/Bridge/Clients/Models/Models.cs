// Bridge/Contracts/Models.cs
namespace Bridge.Contracts;

public sealed record ProjectLink(
    int RedmineProjectId,
    string RedmineIdentifier,
    string Name,
    string? GitLabUrl,            // may be null if CF absent
    string? GitLabPathWithNs      // null if GitLabUrl is null
);

public sealed record IssueBasic(
    int? RedmineId,               // Redmine: set; GitLab: null
    long? GitLabIid,              // GitLab project-scoped IID; Redmine: null
    string Title,
    string? Description
);

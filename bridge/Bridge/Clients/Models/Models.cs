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
    int?  RedmineId,
    long? GitLabIid,
    string Title,
    string? Description,
    string? TrackerName = null,              // <- used by seeder to filter RM
    IReadOnlyList<string>? Labels = null     // <- used by seeder to map GL -> RM tracker
);



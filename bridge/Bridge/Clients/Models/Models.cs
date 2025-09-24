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
    int? RedmineId,
    long? GitLabIid,
    string Title,
    string? Description,
    List<string>? Labels,
    int? AssigneeId = null,
    DateTime? DueDate = null,
    string? Status = null,
    DateTimeOffset? UpdatedAtUtc = null
);



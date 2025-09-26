namespace Bridge.Contracts
{


    public sealed record ProjectLink(
        int RedmineProjectId,
        string RedmineIdentifier,
        string Name,
        string? GitLabUrl,
        string? GitLabPathWithNs
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
}


namespace Bridge.Infrastructure.Options
{
    public sealed class TrackersKeys : List<string> { }

    public sealed class RedmineOptions
    {
        public const string SectionName = "Redmine";
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string PublicUrl { get; set; } = "";
        public string GitlabCustomField { get; set; } = "Gitlab Repo";

    }

    public sealed class RedminePollingOptions
    {
        public bool Enabled { get; set; } = true;
        public int IntervalSeconds { get; set; } = 60; // 1 minute default
        public int JitterSeconds { get; set; } = 5;
    }



    public sealed class GitLabOptions
    {
        public const string SectionName = "GitLab";
        public string BaseUrl { get; set; } = "";
        public string PrivateToken { get; set; } = "";
        public string WebhookSecret { get; set; } = "";
        public string PublicUrl { get; set; } = "";
    }
}
namespace Bridge.Infrastructure.Options;
public sealed class GitLabOptions
{
    public const string SectionName = "GitLab";
    public string BaseUrl { get; set; } = "";
    public string PrivateToken { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
}

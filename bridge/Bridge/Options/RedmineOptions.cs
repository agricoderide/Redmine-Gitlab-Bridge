namespace Bridge.Infrastructure.Options;
public sealed class RedmineOptions
{
    public const string SectionName = "Redmine";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

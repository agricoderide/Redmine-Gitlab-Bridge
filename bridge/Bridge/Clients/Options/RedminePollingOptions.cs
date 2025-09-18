namespace Bridge.Infrastructure.Options;

public sealed class RedminePollingOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60; // 1 minute default
    public int JitterSeconds { get; set; } = 5;    // small random spread to avoid thundering herd
}

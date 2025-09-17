using Microsoft.Extensions.Logging;

namespace Bridge.Services;

public sealed class RedminePoller : IRedminePoller
{
    private readonly RedmineClient _redmine;
    private readonly ILogger<RedminePoller> _logger;

    public RedminePoller(RedmineClient redmine, ILogger<RedminePoller> logger)
    {
        _redmine = redmine;
        _logger = logger;
    }

    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        // TODO: replace with: fetch updated issues since last watermark, route, write to GitLab, etc.
        var (ok, msg) = await _redmine.PingAsync(ct);
        if (!ok) throw new InvalidOperationException($"Redmine ping failed: {msg}");
        _logger.LogInformation("Redmine poll OK: {Msg}", msg);
    }
}

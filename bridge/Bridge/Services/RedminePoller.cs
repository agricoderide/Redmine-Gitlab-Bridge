using Bridge.Services;

public sealed class RedminePoller : IRedminePoller
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RedminePoller> _logger;

    public RedminePoller(IServiceScopeFactory scopeFactory, ILogger<RedminePoller> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
        await sync.RunOnceAsync(ct);
        _logger.LogInformation("Poll pass completed in {ms} ms",
            (DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Options;
using Bridge.Infrastructure.Options;

namespace Bridge.Services;

public sealed class RedminePollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RedminePollingOptions> _opts;
    private readonly ILogger<RedminePollingWorker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static DateTimeOffset? LastRunUtc { get; private set; }
    public static DateTimeOffset? LastSuccessUtc { get; private set; }
    public static int ConsecutiveFailures { get; private set; }

    public RedminePollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RedminePollingOptions> opts,
        ILogger<RedminePollingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Redmine polling worker starting…");

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _opts.CurrentValue;
            if (!cfg.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var interval = TimeSpan.FromSeconds(Math.Max(5, cfg.IntervalSeconds));
            var jitter = TimeSpan.FromSeconds(cfg.JitterSeconds > 0 ? Random.Shared.Next(0, cfg.JitterSeconds) : 0);

            try
            {
                if (await _gate.WaitAsync(TimeSpan.Zero, stoppingToken))
                {
                    try
                    {
                        LastRunUtc = DateTimeOffset.UtcNow;
                        var sw = Stopwatch.StartNew();

                        await PollOnceAsync(stoppingToken);

                        sw.Stop();
                        LastSuccessUtc = DateTimeOffset.UtcNow;
                        ConsecutiveFailures = 0;
                        _logger.LogInformation("Redmine poll finished in {Elapsed} ms", sw.ElapsedMilliseconds);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                else
                {
                    _logger.LogWarning("Previous poll still running; skipping this tick.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // graceful shutdown
            }
            catch (Exception ex)
            {
                ConsecutiveFailures++;
                _logger.LogError(ex, "Redmine poll failed (consecutive={Count})", ConsecutiveFailures);
            }

            try
            {
                await Task.Delay(interval + jitter, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        _logger.LogInformation("Redmine polling worker stopping.");
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        using var scope = _scopeFactory.CreateScope();

        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
        await sync.RunOnceAsync(ct);

        _logger.LogInformation("Poll pass completed in {ms} ms",
            (DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }
}

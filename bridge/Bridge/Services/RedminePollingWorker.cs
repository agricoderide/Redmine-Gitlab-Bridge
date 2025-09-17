using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Bridge.Infrastructure.Options;

namespace Bridge.Services;

public sealed class RedminePollingWorker : BackgroundService
{
    private readonly IRedminePoller _poller;
    private readonly IOptionsMonitor<RedminePollingOptions> _opts;
    private readonly ILogger<RedminePollingWorker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static DateTimeOffset? LastRunUtc { get; private set; }
    public static DateTimeOffset? LastSuccessUtc { get; private set; }
    public static int ConsecutiveFailures { get; private set; }

    public RedminePollingWorker(IRedminePoller poller, IOptionsMonitor<RedminePollingOptions> opts, ILogger<RedminePollingWorker> logger)
    {
        _poller = poller;
        _opts = opts;
        _logger = logger;
    }

    // Just a timer that keeps on running after a certain time given by the configuration file.


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
                // prevent overlap if one run takes longer than the interval
                if (await _gate.WaitAsync(TimeSpan.Zero, stoppingToken))
                {
                    try
                    {
                        LastRunUtc = DateTimeOffset.UtcNow;
                        var sw = Stopwatch.StartNew();
                        await _poller.PollOnceAsync(stoppingToken); // go grabs something from there
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ConsecutiveFailures++;
                _logger.LogError(ex, "Redmine poll failed (consecutive={Count})", ConsecutiveFailures);
            }

            try
            {
                await Task.Delay(interval + jitter, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("Redmine polling worker stopping.");
    }
}

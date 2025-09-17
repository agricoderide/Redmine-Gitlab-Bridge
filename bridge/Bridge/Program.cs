using System.Net;
using System.Text.Json;
using Bridge.Data;
using Bridge.Infrastructure.Options;
using Bridge.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly;
using Polly.Extensions.Http;

namespace Bridge;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Load appsettings.json + appsettings.{ENV}.json automatically
        // ENV is from ASPNETCORE_ENVIRONMENT (Development/Production/etc.)

        // Bind options
        builder.Services.Configure<RedmineOptions>(builder.Configuration.GetSection(RedmineOptions.SectionName));
        builder.Services.Configure<GitLabOptions>(builder.Configuration.GetSection(GitLabOptions.SectionName));

        // Polly retry with jitter
        static IAsyncPolicy<HttpResponseMessage> RetryPolicy() =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(r => (int)r.StatusCode == 429 || r.StatusCode == HttpStatusCode.ServiceUnavailable)
                .WaitAndRetryAsync(5,
                    attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt))
                              + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)));

        // Typed HTTP clients
        builder.Services.AddHttpClient<RedmineClient>().AddPolicyHandler(RetryPolicy());
        builder.Services.AddHttpClient<GitLabClient>().AddPolicyHandler(RetryPolicy());

        // after you configure options and HttpClients…
        builder.Services.Configure<RedminePollingOptions>(
            builder.Configuration.GetSection("Polling:Redmine"));

        builder.Services.AddSingleton<IRedminePoller, RedminePoller>();
        builder.Services.AddHostedService<RedminePollingWorker>();

        // Health + Swagger
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // Database
        builder.Services.AddDbContext<SyncDbContext>(options =>
        {
            var conn = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? "Data Source=bridge.db"; // SQLite for dev
            options.UseSqlite(conn);
        });
        // Seeder
        builder.Services.AddScoped<DbSeeder>();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        using (var scope = app.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
            await seeder.SeedAsync();
        }

        // Health
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/admin/seed", async (DbSeeder seeder, CancellationToken ct) =>
        {
            await seeder.SeedAsync(ct);
            return Results.Ok(new { seeded = true });
        });
        // Dev pings
        app.MapGet("/dev/redmine/ping", async (RedmineClient client, CancellationToken ct) =>
        {
            var (ok, msg) = await client.PingAsync(ct);
            return ok ? Results.Ok(new { ok, msg }) : Results.Problem(msg, statusCode: 502);
        });



        app.MapGet("/dev/redmine/poll/status", () =>
        {
            return Results.Ok(new
            {
                RedminePollingWorker.LastRunUtc,
                RedminePollingWorker.LastSuccessUtc,
                RedminePollingWorker.ConsecutiveFailures
            });


        });


        app.MapPost("/dev/redmine/poll-once", async (IRedminePoller poller, CancellationToken ct) =>
        {
            await poller.PollOnceAsync(ct);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/dev/gitlab/ping", async (GitLabClient client, CancellationToken ct) =>
        {
            var (ok, msg) = await client.PingAsync(ct);
            return ok ? Results.Ok(new { ok, msg }) : Results.Problem(msg, statusCode: 502);
        });

        // GitLab webhook (checks X-Gitlab-Token)
        app.MapPost("/webhooks/gitlab", async (HttpRequest req, IConfiguration cfg) =>
        {
            var secret = cfg.GetSection(GitLabOptions.SectionName)["WebhookSecret"] ?? "";
            if (string.IsNullOrWhiteSpace(secret))
                return Results.Problem("Webhook secret not configured", statusCode: 500);

            if (!req.Headers.TryGetValue("X-Gitlab-Token", out var token) || token != secret)
                return Results.Unauthorized();

            var eventName = req.Headers["X-Gitlab-Event"].ToString();
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            app.Logger.LogInformation("GitLab webhook received: {Event} len={Len}", eventName, body.Length);

            return Results.Ok(new { received = true, eventName, length = body.Length });
        });

        app.Run();
    }
}

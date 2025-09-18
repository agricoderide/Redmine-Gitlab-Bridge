// Bridge application startup: config binding, DI registrations,
// HTTP resilience, EF Core, background workers, health, Swagger,
// reverse-proxy headers, and minimal API endpoints.
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
        // Create the host builder (loads configuration, logging, etc.)
        var builder = WebApplication.CreateBuilder(args);

        // Note: .NET automatically loads appsettings.json + appsettings.{ENV}.json
        // where ENV is ASPNETCORE_ENVIRONMENT (Development/Production/etc.)

        // Bind strongly-typed options from configuration for DI
        builder.Services.Configure<RedmineOptions>(builder.Configuration.GetSection(RedmineOptions.SectionName));
        builder.Services.Configure<GitLabOptions>(builder.Configuration.GetSection(GitLabOptions.SectionName));

        // Resilience policy for outbound HTTP calls (exponential backoff + jitter)
        static IAsyncPolicy<HttpResponseMessage> RetryPolicy() =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(r => (int)r.StatusCode == 429 || r.StatusCode == HttpStatusCode.ServiceUnavailable)
                .WaitAndRetryAsync(5,
                    attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt))
                              + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)));

        // Typed HttpClients for external services, using the above retry policy
        builder.Services.AddHttpClient<RedmineClient>().AddPolicyHandler(RetryPolicy());
        builder.Services.AddHttpClient<GitLabClient>().AddPolicyHandler(RetryPolicy());

        // Redmine polling configuration + background services
        builder.Services.Configure<RedminePollingOptions>(
            builder.Configuration.GetSection("Polling:Redmine"));

        builder.Services.AddSingleton<IRedminePoller, RedminePoller>();
        builder.Services.AddHostedService<RedminePollingWorker>();

        // Health checks and Swagger/OpenAPI generator
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // Database (EF Core): configure SyncDbContext
        builder.Services.AddDbContext<SyncDbContext>(options =>
        {
            var conn = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? "Data Source=bridge.db"; // SQLite for dev
            options.UseSqlite(conn);
        });
        // Database seeding util
        builder.Services.AddScoped<DbSeeder>();

        

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            // Only expose Swagger UI in Development
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Respect proxy headers (X-Forwarded-For/Proto) when behind reverse proxies
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        // Run startup seeding inside a scope
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



        // Background polling status snapshot
        app.MapGet("/dev/redmine/poll/status", () =>
        {
            return Results.Ok(new
            {
                RedminePollingWorker.LastRunUtc,
                RedminePollingWorker.LastSuccessUtc,
                RedminePollingWorker.ConsecutiveFailures
            });


        });


        // Trigger a single poll cycle (manual)
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

        // GitLab webhook (validates shared secret via X-Gitlab-Token)
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

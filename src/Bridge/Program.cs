using System.Net;
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

        // (A) EXPLICIT CONFIG ORDER (env vars always win)
        builder.Configuration
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables();

        builder.Services.Configure<RedmineOptions>(builder.Configuration.GetSection("Redmine"));
        builder.Services.Configure<GitLabOptions>(builder.Configuration.GetSection("GitLab"));
        builder.Services.Configure<TrackersKeys>(builder.Configuration.GetSection("Trackers")); // note: was "Trackers" in your file
        builder.Services.Configure<RedminePollingOptions>(builder.Configuration.GetSection("RedminePolling"));

        static IAsyncPolicy<HttpResponseMessage> RetryPolicy() =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(r => (int)r.StatusCode == 429 || r.StatusCode == HttpStatusCode.ServiceUnavailable)
                .WaitAndRetryAsync(5,
                    attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt))
                              + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)));



        builder.Services.AddHttpClient<RedmineClient>().AddPolicyHandler(RetryPolicy());
        builder.Services.AddHttpClient<GitLabClient>().AddPolicyHandler(RetryPolicy());
        builder.Services.AddScoped<SyncService>();
        builder.Services.AddHostedService<RedminePollingWorker>();

        // Health checks and Swagger/OpenAPI generator
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddDbContext<SyncDbContext>(opt =>
{
    var cs = builder.Configuration.GetConnectionString("SyncDb")
             ?? builder.Configuration["ConnectionStrings__SyncDb"];
    opt.UseNpgsql(cs, npg => npg.EnableRetryOnFailure());
});

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            // Only expose Swagger UI in Development
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });





        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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

        app.MapGet("/dev/gitlab/ping", async (GitLabClient client, CancellationToken ct) =>
        {
            var (ok, msg) = await client.PingAsync(ct);
            return ok ? Results.Ok(new { ok, msg }) : Results.Problem(msg, statusCode: 502);
        });


        app.Run();
    }
}

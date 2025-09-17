using Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Bridge.Data;

public sealed class SyncDbContextFactory : IDesignTimeDbContextFactory<SyncDbContext>
{
    public SyncDbContext CreateDbContext(string[] args)
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{env}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var conn = configuration.GetConnectionString("DefaultConnection")
                   ?? "Data Source=bridge.db";

        var optionsBuilder = new DbContextOptionsBuilder<SyncDbContext>();
        optionsBuilder.UseSqlite(conn);

        return new SyncDbContext(optionsBuilder.Options);
    }
}


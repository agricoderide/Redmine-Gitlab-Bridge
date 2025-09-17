dotnet new web -n GlRm.Bridge
cd GlRm.Bridge

# Data + DB
dotnet add package Microsoft.EntityFrameworkCore
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL

# Logging
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Sinks.Console

# Resilience
dotnet add package Polly
dotnet add package Polly.Extensions.Http

# Schedulers
dotnet add package Quartz.Extensions.Hosting

# API clients
dotnet add package GitLabApiClient
dotnet add package redmine-net-api

# Mapping (optional but handy)
dotnet add package Mapster
dotnet add package Mapster.DependencyInjection

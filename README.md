# Redmine ↔ GitLab Bridge

A .NET service that synchronizes **Redmine** and **GitLab** projects.  
It mirrors issues, assignees, statuses, trackers, and inserts a cross-system `Source:` backlink so each issue points to its counterpart.

---

## Contents

- [Features](#features)  
- [How It Works](#how-it-works)  
- [What Gets Synced](#what-gets-synced)  
- [Requirements](#requirements)  
- [Configuration](#configuration)  
  - [appsettings.json](#appsettingsjson)  
  - [Environment Variables](#environment-variables)  
  - [Redmine Setup](#redmine-setup)  
  - [GitLab Setup](#gitlab-setup)  
- [Build & Run](#build--run)  
- [Docker](#docker)  
  - [Profiles](#profiles)  
  - [Ports & URLs](#ports--urls)  
  - [Common Commands](#common-commands)  
- [Database](#database)  
  - [Connection Strings](#connection-strings)  
  - [Managing Databases](#managing-databases)  
  - [Editing from VS Code](#editing-from-vs-code)  
- [Logging](#logging)  
- [Background Polling](#background-polling)  
- [Optional Webhooks](#optional-webhooks)  
- [Project Layout](#project-layout)  
- [Troubleshooting](#troubleshooting)  
- [FAQ](#faq)  
- [License](#license)  

---

## Features

- Two-way synchronization between Redmine and GitLab:
  - Issues: title, description, tracker/labels, assignee, due date, open/closed state
  - Project members mapping (name-based heuristic, persisted)
  - Global reference data (trackers and statuses) auto-refreshed
- Automatic backlinks:
  - In GitLab: `Source: https://redmine.example.com/issues/{id}`
  - In Redmine: `Source: https://gitlab.example.com/group/project/-/issues/{iid}`  
  Updates preserve a single `Source:` line (no duplication).
- Conflict resolution with a **canonical snapshot** per mapping:
  - If one side changed, it wins
  - If both changed, the freshest `UpdatedAtUtc` wins per field
- Idempotent polling worker with jitter and overlap protection
- EF Core persistence with migrations

---

## How It Works

1. **Projects**  
   Reads Redmine projects and a custom project field that stores the GitLab repository URL. Resolves the GitLab numeric project id.  

2. **Members**  
   Fetches members from both systems and persists cross-IDs when names match (simple heuristic; stored in the `Users` table).  

3. **Issues (seed)**  
   Fetches basic issue lists and seeds mappings by exact, case-insensitive title match when unique.  

4. **Create missing**  
   - Redmine → GitLab: creates GitLab issue and adds backlink to Redmine.  
   - GitLab → Redmine: creates Redmine issue and adds backlink to GitLab.  

5. **Reconcile**  
   Compares live values to the canonical snapshot, pushes deltas to the opposite side, then updates the snapshot.  

---

## What Gets Synced

| Field           | Redmine                | GitLab             | Notes                                        |
|-----------------|------------------------|-------------------|----------------------------------------------|
| Title           | `subject`              | `title`            | Two-way                                      |
| Description     | `description`          | `description`      | First line normalized to `Source:` backlink  |
| Tracker / Label | `tracker.name`         | first matching `label` | Driven by `TrackersKeys` config          |
| Assignee        | `assigned_to.id`       | first `assignees[].id` | Uses persisted user mappings             |
| Due Date        | `due_date`             | `due_date`         | ISO date (`YYYY-MM-DD`)                      |
| Status / State  | `status.name` (New/Closed) | `state` (opened/closed) | Mapping: New ↔ opened, Closed ↔ closed |

---

## Requirements

- .NET 8 SDK  
- PostgreSQL (default; other EF Core providers can be configured)  
- Redmine with REST API enabled  
- GitLab with API v4 token (permissions: read/manage issues and members)  

---

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "SyncDb": "Host=localhost;Port=5433;Database=bridge;Username=bridge;Password=bridge"
  },
  "Redmine": {
    "BaseUrl": "http://localhost:12344",
    "ApiKey": "your-redmine-api-key",
    "GitlabCustomField": "GitLab URL",
    "PublicUrl": "http://localhost:12344"
  },
  "GitLab": {
    "BaseUrl": "http://localhost:12345",
    "PrivateToken": "your-gitlab-token"
  },
  "TrackersKeys": [ "Feature", "Bug", "Task" ],
  "RedminePolling": {
    "Enabled": true,
    "IntervalSeconds": 60,
    "JitterSeconds": 10
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Bridge": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

### Environment Variables

All options can be provided as environment variables (`__` for nesting):

- `ConnectionStrings__SyncDb`  
- `Redmine__BaseUrl`, `Redmine__ApiKey`, `Redmine__GitlabCustomField`, `Redmine__PublicUrl`  
- `GitLab__BaseUrl`, `GitLab__PrivateToken`  
- `RedminePolling__Enabled`, `RedminePolling__IntervalSeconds`, `RedminePolling__JitterSeconds`  

The `docker-compose.yml` also supports `DB_CONN` to populate `ConnectionStrings__SyncDb`.

### Redmine Setup

1. Create a **Project Custom Field** named exactly as `Redmine.GitlabCustomField` (e.g., `GitLab URL`).  
2. Populate the field in each project with the GitLab repository URL (e.g., `https://gitlab.example.com/group/project`).  
3. Ensure the API user:
   - Can view projects and members  
   - Can create/edit issues  
   - Has a role that is **assignable** (Administration → Roles and permissions → enable “Issues can be assigned to this role”).  

### GitLab Setup

1. Token in `GitLab.PrivateToken` must allow project access, member listing, and issue create/update/close.  
2. For webhooks, configure GitLab to call the Bridge’s external endpoint.  

---

## Build & Run

```bash
dotnet restore
dotnet build -c Release
dotnet ef database update
dotnet run
```

On startup:  
- Migrates database  
- Refreshes trackers and statuses  
- Discovers projects  
- Starts polling worker  

---

## Docker

`docker-compose.yml` defines Redmine, GitLab CE, PostgreSQL, and the Bridge app. Services are grouped by **profiles**.

### Profiles

- `core` — database and core dependencies  
- `app` — Redmine and GitLab CE (for testing)  
- `bridge` — the Bridge application  

### Ports & URLs

| Service  | Container URL        | Host URL              |
|----------|----------------------|-----------------------|
| Redmine  | http://redmine:3000  | http://<your_ip>:12344 |
| GitLab   | http://gitlab        | http://<your_ip>:12345 |
| Bridge   | http://bridge:5001   | http://<your_ip>:12346 |
| Postgres | bridge-db:5432       | localhost:5433        |

### Common Commands

```bash
# List services (with profiles)
docker compose --profile core --profile app --profile bridge config --services

# Show running containers
docker compose --profile core --profile app --profile bridge ps

# Start full stack
docker compose --profile core --profile app --profile bridge up -d

# Rebuild just the Bridge
COMPOSE_PROFILES=core,app,bridge docker compose up -d --build bridge

# View Bridge logs
docker compose --profile core --profile app logs -f bridge

# Stop Bridge only
docker compose --profile core --profile app --profile bridge stop bridge
```

---

## Database

### Connection Strings

**Inside containers:**  
```
Host=bridge-db;Port=5432;Database=bridge;Username=bridge;Password=bridge
```

**From host (5433:5432 mapping):**  
```
Host=localhost;Port=5433;Database=bridge;Username=bridge;Password=bridge
```

### Managing Databases

```bash
# Connect as postgres
docker compose exec -u postgres bridge-db psql

# Drop an old DB
REVOKE CONNECT ON DATABASE bridge_local FROM PUBLIC;
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'bridge_local';
DROP DATABASE IF EXISTS bridge_local;

# List databases
\l
```

### Editing from VS Code

Recommended extensions:  
- PostgreSQL (Microsoft) — `ms-ossdata.postgresql`  
- SQLTools + PostgreSQL driver — `mtxr.sqltools`, `mtxr.sqltools-driver-pg`  
- Optional: Database Client (cweijan)  

Example SQLTools connection:

```json
{
  "sqltools.connections": [
    {
      "name": "Bridge (Docker)",
      "driver": "PostgreSQL",
      "server": "localhost",
      "port": 5433,
      "database": "bridge",
      "username": "bridge",
      "password": "bridge"
    }
  ]
}
```

---

## Logging

- Default: ASP.NET Core logs → console → Docker JSON logs  
- View logs:  
  ```bash
  docker compose logs -f bridge
  ```  
- To persist logs, add a file sink (e.g., Serilog) writing to `/logs` and mount a host volume:  

```yaml
bridge:
  volumes:
    - ./logs:/logs
```

---

## Background Polling

The worker runs every `RedminePolling.IntervalSeconds` ± `JitterSeconds`.  
Each pass:  
- Refreshes trackers and statuses  
- Discovers projects and members  
- Seeds mappings by unique title  
- Creates missing issues  
- Reconciles mapped pairs  

---

## Project Layout

```
src/Bridge/                # Application
src/Bridge/Data/           # EF Core DbContext, entities, migrations
src/Bridge/Services/       # Sync logic
Dockerfile.bridge          # Bridge container build
docker-compose.yml         # Local stack
README.md                  # This file
```

---

## Troubleshooting

- **422 “Assignee is invalid”**  
  User must be project member with assignable role.  

- **422 “Due date must be greater than start date”**  
  Redmine defaults `start_date` to today. Ensure `due_date > start_date` or omit due on create.  

- **404 GitLab project resolution**  
  Check custom field holds correct GitLab path (`group/project`).  

- **404 Redmine memberships**  
  API user lacks access or project archived. Use identifier in `/projects/{id_or_identifier}/memberships.json`.  

- **Compose shows no services**  
  All behind profiles. Pass `--profile` flags or set `COMPOSE_PROFILES`.  

- **“no such service” stopping**  
  Use same project name and profiles as when started, or stop by container name.  

- **Duplicate issues**  
  Titles must be unique for seeding.  

---

## FAQ

**Does this replace Redmine or GitLab?**  
No. It synchronizes issues between systems.

**What if both sides edit the same field?**  
The freshest `UpdatedAtUtc` wins per field.

**Can I sync more fields?**  
Yes. Extend `IssueBasic` and reconciliation.

**Do I need both systems running?**  
Yes. The bridge requires live Redmine and GitLab.

---

## License

MIT License

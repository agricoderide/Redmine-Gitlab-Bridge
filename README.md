# Redmine ↔ GitLab Bridge

A .NET service that keeps **Redmine** and **GitLab** projects in sync.  
It mirrors issues, assignees, statuses, and trackers, and automatically inserts a cross-system `Source:` backlink so each issue points to its counterpart.

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
- [Database](#database)
- [Background Polling](#background-polling)
- [Optional Webhooks](#optional-webhooks)
- [Project Layout](#project-layout)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [License](#license)

---

## Features

- Two-way synchronization between Redmine and GitLab
  - Issues: title, description, tracker/labels, assignee, due date, open/closed
  - Project members mapping (name-based heuristic, persisted)
  - Global reference data (trackers and statuses) auto-refreshed
- Automatic backlinks  
  On creation, the description is normalized so the **first line** is:
  - In GitLab: `Source: https://redmine.example.com/issues/{id}`
  - In Redmine: `Source: https://gitlab.example.com/group/project/-/issues/{iid}`
  Updates preserve a single `Source:` line (no duplication).
- Conflict resolution with a **canonical snapshot** per mapping
  - If one side changed, it wins
  - If both changed, field-by-field winner uses the freshest `UpdatedAtUtc`
- Idempotent polling worker with jitter and overlap protection
- EF Core persistence with migrations

---

## How It Works

1. **Projects**: Reads Redmine projects and a custom field that stores GitLab repo URL. Resolves the GitLab numeric `projectId`.
2. **Members**: Pulls members from both systems and stores cross-IDs when names match.
3. **Issues (seed)**  
   - Fetches *basic* issue data lists from both sides.
   - Seeds mappings by exact, case-insensitive title match (1:1).
4. **Create missing**  
   - If an issue exists in Redmine but not GitLab → creates in GitLab (with `Source:` pointing to Redmine).
   - If an issue exists in GitLab but not Redmine → creates in Redmine (with `Source:` pointing to GitLab).
5. **Reconcile**  
   - Compares live values vs. the canonical snapshot.
   - Pushes deltas to the opposite system and updates the canonical snapshot.

---

## What Gets Synced

| Field            | Redmine                        | GitLab                         | Notes |
|------------------|--------------------------------|--------------------------------|------|
| Title            | `subject`                      | `title`                        | Two-way |
| Description      | `description`                  | `description`                  | First line normalized to single `Source:` backlink |
| Tracker/Label    | `tracker.name`                 | first matching `label`         | `TrackersKeys` config drives which labels count as trackers |
| Assignee         | `assigned_to.id`               | first `assignees[].id`         | Uses persisted user mapping table |
| Due Date         | `due_date`                     | `due_date`                     | ISO date |
| Status / State   | `status.name` (e.g., New/Closed) | `state` (opened/closed)        | Simple mapping: New↔opened, Closed↔closed |

---

## Requirements

- .NET 8 SDK
- PostgreSQL (or another EF Core supported provider you configure)
- Redmine with API enabled
- GitLab with a personal/project access token (API v4)

---

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=bridge;Username=bridge;Password=bridge"
  },
  "Redmine": {
    "BaseUrl": "https://redmine.example.com",
    "ApiKey": "your-redmine-api-key",
    "GitlabCustomField": "GitLab URL"
  },
  "GitLab": {
    "BaseUrl": "https://gitlab.example.com",
    "PrivateToken": "your-gitlab-token"
  },
  "TrackersKeys": [
    "Feature",
    "Bug",
    "Task"
  ],
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








Redmine Setup

Create a Project Custom Field (format: string or link) named exactly as Redmine.GitlabCustomField, e.g., GitLab URL.

For each Redmine project to sync, populate that custom field with the GitLab repository URL (e.g., https://gitlab.example.com/group/project).

GitLab Setup

Ensure the token in GitLab.PrivateToken has permissions to read projects and manage issues.

If you enable webhooks (optional), allow POSTs to your service’s externally reachable endpoint.



Build & Run

Install dependencies and build:

dotnet restore
dotnet build -c Release


Run EF Core migrations:

dotnet ef database update


Start the service:

dotnet run


On startup:

Ensures database is migrated

Syncs Redmine global trackers and statuses

Discovers projects

Polling worker begins periodic sync passes



 docker compose --profile core up -d
then you go find the api keys for redmine and gitlab
docker compose --profile core --profile app --profile bridge up -d


when you make changes to c#
COMPOSE_PROFILES=core,app,bridge docker compose up -d --build bridge
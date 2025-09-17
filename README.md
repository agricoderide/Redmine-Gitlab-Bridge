# Redmine ⇄ GitLab Lab (Pinned) + FastAPI Bridge

## 1) Start
cp .env.example .env
# fill tokens/secrets in .env
make up
make logs

## 2) Redmine
- http://localhost:3000
- Create admin user, get API key: My account → API access key
- (Optional) Install plugins in redmine-plugins volume and restart:
  - redmine_gitlab_hook (receive GitLab push & write notes)
  - redmine_webhook (emit issue create/update to bridge)

Configure Redmine WebHook (plugin):
- Target URL: http://bridge:5001/redmine
- Secret: REDMINE_WEBHOOK_SECRET from .env
- Events: issue create/update (and any you want)
- Enable “Send custom header signature” if plugin supports it (HMAC SHA256)

## 3) GitLab
- http://localhost:8080
- Read /etc/gitlab/initial_root_password inside container to sign in as root
- Create Personal Access Token (scope: api) → put into GITLAB_TOKEN
- Project → Settings → Integrations → Redmine:
  - Redmine URL: http://localhost:3000
  - Issue URL: http://localhost:3000/issues/:id
  - New issue URL: http://localhost:3000/projects/<identifier>/issues/new
  - (Optionally) Disable GitLab Issues so #123 always points to Redmine
- Project → Settings → Webhooks:
  - URL: http://bridge:5001/gitlab
  - Secret Token: GITLAB_WEBHOOK_SECRET
  - Select events: Push, Merge requests (and optionally Pipeline)

## 4) Test
- In Redmine: create project with identifier `demo-redmine`, create issue #1
- In GitLab: create project, commit with message "feat: login (refs #1)"
- Bridge should log events; if MIRROR_STRATEGY=mirror, a shadow issue is created/updated in GitLab
- Merge an MR referencing #1 → Redmine gets a journal note

## 5) Notes
- Keep postgres pinned (16). Back up volumes before upgrades.
- Set MIRROR_STRATEGY=none if you only want note logging (no mirroring).




we need to set a bunch of stuff in administration panel of redmine before we can start creating new issues
and log time and stuff
admin
admin

to enter gitlab is: localhost:8888 and then
root
sudo docker exec -it ad64  cat /etc/gitlab/initial_root_password     --- to check the pass












Create a new Redmine project → set GitLabRepo field.

Create/clone a GitLab project → create CI/CD var REDMINE_KEY with Redmine identifier.

The bridge discovers and syncs automatically within ≤ 5 minutes (or instantly for GitLab-origin traffic via webhook).



Enable in settings api rest web service
Redmine	Project custom field (string)	GitLabRepo	Administration ▸ Custom fields ▸ Projects	mygroup/demo
<!-- GitLab	CI/CD variable (Project → Settings → CI/CD → Variables)	REDMINE_KEY	scope “Protected = off, Masked = off”	demo -->


settings outbound requests allow local just for test for now.
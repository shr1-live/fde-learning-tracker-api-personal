# FDE Tracker API

Backend for the FDE Learning Tracker. **ASP.NET Core on .NET 10** (MVC-style Web API), Postgres (SQL migrations in `migrations/`). Deployed on Render as a Docker service; the frontend is a separate repo on Vercel.

## What it does

- **Access:** verifies the Supabase sign-in token. The admin and the first 3 other people are approved automatically; everyone after that waits for the admin.
- **Content:** members submit articles, videos, links or notes for a topic (general, or for one tech stack). Nothing is public until a reviewer or the admin approves it.
- **Feed:** approved items only, by topic, with cursor paging. Everyone sees general items; picking a stack adds that stack's items.
- **Programs:** the shared six-week programme, plus each person's own private plans, with per-person progress.
- **Admin:** approve or reject people, make someone a reviewer.

## Structure (MVC)

```
FdeTracker.Api/
  Controllers/    C: HTTP only. Read the request, call a service, return a view model.
  Models/         M: domain records (AppUser, ContentRecord ...) and Requests/ (validated input DTOs).
  Services/       M: business rules and transactions (limits, review rules, access seats).
  Repositories/   M: all SQL (Dapper). One class per table group.
  ViewModels/     V: the JSON shapes the API returns (no internal ids leak).
  Filters/        RequireApproved and RequireRole, run before model binding.
  Middleware/     error handling, security headers, token check, current-user loading.
  Data/           connection pool, transactions, migration runner.
FdeTracker.Tests/ xUnit integration tests against a real Postgres (Testcontainers).
migrations/       SQL, applied once each at start-up.
```

Request flow: route -> filters (approved / role) -> controller -> service (rules, transaction) -> repository (SQL) -> view model -> JSON (snake_case).

## Security model

- Every `/api` route needs a valid token; the account must be on the allowed email domain.
- Roles: `member`, `reviewer`, `admin`. The admin is the email in `ADMIN_EMAIL` and cannot be demoted by the API.
- Input is validated (data annotations, unknown fields refused); SQL is always parameterised; request bodies are capped at 64 KB.
- Rate limits per IP and per user (writes); caps on drafts and plans per person.
- Security headers; CORS allow-list (`CORS_ORIGINS`); errors never leak internals; an audit log records reviews and admin actions.
- Secrets live only in environment variables.

## Settings (environment variables)

| Name | Needed | Meaning |
|---|---|---|
| `DATABASE_URL` | yes | `postgresql://user:pass@host:5432/db` (SSL is required for remote hosts; add `?sslmode=disable` for a plain local Postgres) |
| `SUPABASE_URL` | yes | your Supabase project URL |
| `SUPABASE_JWT_SECRET` | no | only for the legacy shared-secret (HS256) projects |
| `CORS_ORIGINS` | yes | comma-separated frontend origins |
| `ALLOWED_DOMAIN`, `ADMIN_EMAIL`, `FREE_SEATS` | no | defaults in `render.yaml` |
| `PORT` | no | set by Render |

## Run locally

```bash
# needs the .NET 10 SDK
export DATABASE_URL="postgresql://postgres:pw@localhost:5432/postgres"   # PowerShell: $env:DATABASE_URL = "..."
export SUPABASE_URL="https://YOUR-PROJECT.supabase.co"
dotnet run --project FdeTracker.Api
dotnet test        # needs Docker running: starts a throwaway Postgres
```

## Deploy on Render (free)

1. Push this repo to GitHub.
2. Render > New > Blueprint > pick the repo (it reads `render.yaml` and builds the `Dockerfile`).
3. Set the three secret env vars: `DATABASE_URL`, `SUPABASE_URL`, `CORS_ORIGINS`.
4. Use a Postgres that persists. Render's free Postgres expires after 30 days, so the Supabase database (free, persistent) is the recommended `DATABASE_URL`. Use its connection string in the Render dashboard only.
5. Free web services sleep after 15 minutes idle; the first request after that takes about 30 seconds.

## Endpoints

| Method | Path | Who |
|---|---|---|
| GET | `/health`, `/api/stacks`, `/api/topics` | public |
| GET / PATCH | `/api/me` | signed in (PATCH: approved) |
| GET | `/api/feed?topic=&stack=&limit=&before=` | approved |
| GET / POST | `/api/content/mine`, `/api/content/{id}`, `/api/content` | approved |
| PATCH / DELETE | `/api/content/{id}` | author |
| GET | `/api/review/queue` | reviewer, admin |
| POST | `/api/review/{id}` | reviewer, admin |
| GET / POST | `/api/programs`, `/api/programs/{slug}` | approved |
| POST / DELETE | `/api/programs/{slug}/items[/{id}]` | plan owner |
| PUT | `/api/programs/{slug}/items/{id}/progress` | approved |
| GET / POST | `/api/admin/users`, `/api/admin/users/{id}/access`, `/api/admin/users/{id}/role` | admin |

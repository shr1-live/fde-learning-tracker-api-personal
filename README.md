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

## Recruitment assistant (LLM feature)

`POST /api/assistant/analyze` takes a job description and 1 to 5 candidate profiles and returns, for each candidate: a fit rating, matched skills **each with a quote from the profile**, gaps, and what the profile does not say. It is for approved users only, with a daily cap per person.

How it protects the result:

- Job and candidate text is wrapped in tags with angle brackets escaped, and the prompt says it is data, never instructions.
- The model's answer must be in a fixed shape. Every quote is checked against the profile; a claim without a real quote is removed and reported as a warning. For known skills the quote must mention that skill.
- Two plain functions (find skills, score a match) give an independent keyword check next to the model's answer.
- Anything unexpected from the model becomes a clean "unusable answer" (502), never a crash. Provider errors, timeouts and rate limits become clear messages; the provider's body and the key are never shown.
- Every call is logged (model, prompt version, tokens, latency, status; no prompt text) and counts toward the daily cap, including failed and cancelled calls.

Prompt versions live in `FdeTracker.Api/Prompts/analyze.v1.md` to `v3.md`; `v2` is the default because it scored best in the Day 2 comparison (see `docs/evals/day2-local-comparison.md`); any version can be chosen per request.

### Assistant settings (environment variables only)

| Name | Meaning |
|---|---|
| `LLM_PROVIDER` | `openai` (default: any provider with the common chat-completions API, such as Gemini, Groq, Ollama) or `anthropic` (Claude) |
| `LLM_BASE_URL` | the provider's address; needed for `openai`, must NOT be set for `anthropic` |
| `LLM_API_KEY` | the key (any non-empty text for a local Ollama). Never put it in code, in the repo or in chat |
| `LLM_MODEL` | the model name |
| `LLM_DAILY_CAP` | analyses per person per 24 hours (default 20) |
| `LLM_TIMEOUT_SECONDS`, `LLM_RETRY_DELAY_MS` | per-call timeout (default 30) and retry wait (default 1000) |

Without `LLM_API_KEY` and `LLM_MODEL` the endpoint answers 503 and nothing else is affected.

### Try it with a free local model (Ollama)

```bash
ollama pull qwen2.5:3b
export LLM_BASE_URL=http://127.0.0.1:11434/v1 LLM_API_KEY=local LLM_MODEL=qwen2.5:3b LLM_TIMEOUT_SECONDS=180
dotnet run --project FdeTracker.Api      # with DATABASE_URL and SUPABASE_URL set as above
```

### Comparing models and prompt versions (Evals)

`FdeTracker.Evals` is a command-line tool. Which models run is listed in `evals/targets.json` (name, provider, address, model, and the NAME of the environment variable that holds the key). A model without its key is skipped with a message; local ones need no key.

```bash
dotnet run --project FdeTracker.Evals -- day1    # same fixed prompt set on every model: speed, tokens, pass or fail
dotnet run --project FdeTracker.Evals -- day2    # every prompt version on the same candidates, twice, to check repeatability
# EVAL_ONLY=claude  picks one target; EVAL_RUN=name labels the output files; ANTHROPIC_API_KEY adds Claude
```

Results and generated reports are written to `docs/evals/`. The frontend's Week 1 report page reads the same results files.

## Tests

`dotnet test` runs the integration tests against a throwaway Postgres in Docker (Docker must be running) and the client tests against a fake HTTP layer (no network, no key, no cost). The model is always scripted in tests.

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
| GET | `/api/assistant/usage` | approved |
| POST | `/api/assistant/analyze` | approved, daily cap |
| GET / POST | `/api/admin/users`, `/api/admin/users/{id}/access`, `/api/admin/users/{id}/role` | admin |

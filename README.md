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
- A guardrail in code, whatever the prompt says: a "strong" or "partial" rating with no claim backed by a real quote is lowered to "insufficient information" and a warning is added. This limits what an instruction hidden in a profile can achieve.
- Anything unexpected from the model becomes a clean "unusable answer" (502), never a crash. Provider errors, timeouts and rate limits become clear messages; the provider's body and the key are never shown.
- Every call is logged (model, prompt version, tokens, latency, status; no prompt text) and counts toward the daily cap, including failed and cancelled calls.

Prompt versions live in `FdeTracker.Api/Prompts/analyze.v1.md` to `v4.md`; `v4` is the default because it scored best (94%) for the model in use in the Day 2 comparison (see `docs/evals/day2-local-v2-comparison.md`; on the smaller 1.5b model `v2` won, so re-run the comparison when the model changes); any version can be chosen per request.

### Assistant settings (environment variables only)

| Name | Meaning |
|---|---|
| `LLM_PROVIDER` | `openai` (default: any provider with the common chat-completions API, such as Gemini, Groq, Ollama) or `anthropic` (Claude) |
| `LLM_BASE_URL` | the provider's address; needed for `openai`. Ignored for `anthropic`: Claude is always called at Anthropic's own address, so a stale value can never receive the key |
| `LLM_API_KEY` | the key (any non-empty text for a local Ollama). Never put it in code, in the repo or in chat |
| `LLM_MODEL` | the model name |
| `LLM_DAILY_CAP` | analyses per person per 24 hours (default 20) |
| `LLM_TIMEOUT_SECONDS`, `LLM_RETRY_DELAY_MS` | per-call timeout (default 30) and retry wait (default 1000) |
| `LLM_MAX_TOKENS` | longest answer paid for per call (default 3000); raise it if several candidates get cut off |
| `RATE_LIMIT_PER_MINUTE`, `WRITE_LIMIT_PER_MINUTE` | requests per minute per address (default 240) and writes per minute per person (default 40) |

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
# EVAL_ONLY=claude  runs the paid Claude target (disabled by default so a key in your shell never spends money by accident;
#                   it also needs ANTHROPIC_API_KEY). EVAL_RUN=name labels the output files.
```

Results and generated reports are written to `docs/evals/`. The frontend's Week 1 report page reads the same results files.

## Task assistant (Day 3: tool calling)

`POST /api/task-assistant/message` takes a plain-words request ("add a task: review PR 42, due 2026-12-04, high priority"). The model replies with a fixed JSON request for one tool (`create_task`, `list_tasks`, `update_task`, `delete_task`; prompt in `Prompts/tasks.v1.md`) or with a final answer. Our code does the rest:

- **Validation:** every argument is checked for type, length, allowed values, real dates and unknown names. A bad request is refused, the refusal is fed back to the model once as data, and the loop stops after 4 steps.
- **Ownership:** every query is scoped to the signed-in person. The model cannot name a person or reach someone else's task.
- **Confirmation:** a delete only creates a pending question (`pending_actions`, expires in 10 minutes). `POST /api/task-assistant/confirm/{id}` with `{"approve": true|false}` decides it once, re-checks it, and only then deletes. Ignoring or cancelling it changes nothing. If the task was renamed or removed after the question was asked, the yes is refused (the person approved something else). An answer that comes too late is refused with 410 and logged as expired.
- **Untrusted text:** task titles that come back from a tool reach the model escaped, inside tags, as data.
- **Log:** every call, including refused ones, is stored in `tool_calls` with outcome and speed (`GET /api/task-assistant/log`); `GET /api/task-assistant/tasks` lists the person's tasks.
- **Limits:** counts against the same daily cap as the recruitment assistant (one reservation per message), 200 tasks per person.

A small local model (qwen2.5:3b) handled create, list and update but not delete; see `docs/WEEK1-REPORT.md`. Use a stronger model for real use.

## Guardrails and the Day 4 evaluation

The model's answer is checked in code before anyone sees it (`Services/Llm/AnalysisParser.cs`, `Guardrails.cs`):

1. **Real quotes only.** Every claimed skill needs a quote that is really in the profile and, for a known skill, names that skill. Claims without one are removed and counted.
2. **A rating needs a real quote.** "strong" or "partial" with no backed claim becomes "insufficient_information".
3. **Orders hidden in a profile are ignored (new).** `InstructionScreen` finds text that gives orders to the assistant ("ignore all previous instructions", "you are now...", "print your system prompt", a fake `</candidate>` tag, "output exactly and nothing else"). The sentences that contain them are cut out before the quote check and the skill check, so the injected text cannot vouch for itself. Such a profile cannot be rated above "partial", the model's summary is replaced by a fixed sentence, and "profile contains instructions" is added to what is not stated.
4. **A top rating must match our own skill check (new).** "strong" needs at least half of the job's known skills to be found in the profile by our own code, independent of the model.

`GuardrailOptions` switches these on and off so the evaluation can measure what each adds. The API always uses all of them.

Run the 30-case evaluation (10 normal, 5 incomplete, 5 ambiguous, 5 injection, 5 edge; `evals/day4-cases.json`):

```
dotnet run --project FdeTracker.Evals -- day4
```

`evals/day4-holdout.json` is a second, fresh set (10 new attacks in other wordings and 5 honest profiles) written after the checks and not used to tune them: run it with `EVAL_CASES=day4-holdout.json EVAL_RUN=holdout`. Its result is the fairer one: the new instruction screen caught none of the new attacks, and the older quote rules did the work (see `docs/WEEK1-REPORT.md`). `day4-rescore` re-applies the current checks to the saved raw replies without calling a model.

The model is called once per case and prompt version. Four configurations (no checks, the earlier checks, all checks, all checks with prompt v4) are then scored on the same raw answers. The report lists passes per category, unsupported claims shown, injection obeyed, what was fixed, what got worse and every failure (`docs/evals/day4-*-comparison.md`).

### Cost figures

In `evals/targets.json`, `price_in_per_million` and `price_out_per_million` (US dollars per million tokens, copied from the provider's price page) add a "Cost of this set" column to the Day 1 report. Use 0 for a model that runs on this computer. When no price is set the report says "not set" instead of guessing.

## Submissions and review

A person hands in a task (for example a Week 1 day) with **proof**, and reviewers approve it or send it back.

- **Proof:** https links, or files (PNG, JPG, GIF, WebP, PDF, plain text or ZIP, up to 2 MB each, 8 per submission, 20 MB per person). Files are stored in the database and identified by their own bytes, never by their name or the type the browser claims; names are cleaned; downloads are served as attachments (images inline) with a sandbox policy and `nosniff`.
- **Lifecycle:** draft, then submitted, then approved or changes requested. After changes are requested the person edits and submits again. While a submission waits for review, its owner can withdraw it to edit (not after a decision). Every decision and comment is kept.
- **What the learner writes:** what they did, what they learned and a 1 to 5 self-rating (the programme sheet's columns). Submitting needs proof, the learned text and a rating, and says everything that is missing at once. Saves are partial: a field that is left out is kept, an explicit null clears it.
- **Roles:** a member sees and edits only their own work. Reviewers review: they cannot create or change submissions (403), only read, review and download submitted work. The admin also takes part, so can submit as well. Reviewers and admins see everyone's submitted work (never drafts) and decide on it. A file can be downloaded by its owner and, once submitted, by reviewers and admins; for anyone else it does not exist.
- **Safety:** changes are serialised per person (so parallel uploads cannot pass the storage cap), only the upload route may carry more than 64 KB, and every action is written to the audit log.

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
| POST | `/api/task-assistant/message`, `/confirm/{id}` | approved, daily cap |
| GET | `/api/task-assistant/tasks`, `/log` | approved (own) |
| GET | `/api/submissions` | approved (own) |
| GET / PUT | `/api/submissions/{taskKey}` | approved (own) |
| POST | `/api/submissions/{taskKey}/links`, `/files`, `/submit` | approved (own) |
| DELETE | `/api/submissions/{taskKey}/items/{id}` | approved (own) |
| GET | `/api/submissions/items/{id}/file` | owner; reviewers and admins once submitted |
| GET | `/api/review/submissions[?status=]`, `/counts`, `/{id}` | reviewer, admin |
| POST | `/api/review/submissions/{id}` | reviewer, admin (not for their own work) |
| GET / POST | `/api/admin/users`, `/api/admin/users/{id}/access`, `/api/admin/users/{id}/role` | admin |

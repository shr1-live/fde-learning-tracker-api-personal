# FDE Tracker API

Backend for the FDE Learning Tracker. Node 20+, Express, Postgres (SQL migrations in `migrations/`). Deployed on Render; the frontend is a separate repo on Vercel.

## What it does

- **Access:** verifies the Supabase sign-in token. The admin and the first 3 other people are approved automatically; everyone after that waits for the admin.
- **Content:** members submit articles, videos, links or notes for a topic (general, or for one tech stack). Nothing is public until a reviewer or the admin approves it.
- **Feed:** approved items only, by topic. Everyone sees general items; picking a stack adds that stack's items.
- **Programs:** the shared six-week programme, plus each person's own private plans, with per-person progress.
- **Admin:** approve or reject people, make someone a reviewer.

## Security model

- Every `/api` route needs a valid token; the account must be on the allowed email domain.
- Roles: `member`, `reviewer`, `admin`. The admin is the email in `ADMIN_EMAIL` and cannot be demoted by the API.
- All input is validated with zod; SQL is always parameterised; request bodies are capped at 64 KB.
- Rate limits per IP and per user; caps on drafts and plans per person.
- helmet headers; CORS allow-list (`CORS_ORIGINS`); errors never leak internals; an audit log records reviews and admin actions.
- Secrets live only in environment variables.

## Run locally

```bash
npm install
cp .env.example .env     # fill in real values
npm run migrate
npm start
npm test                 # runs against an in-memory Postgres (PGlite); no database needed
```

## Deploy on Render (free)

1. Push this repo to GitHub.
2. Render > New > Blueprint > pick the repo (it reads `render.yaml`).
3. Set the three secret env vars: `DATABASE_URL`, `SUPABASE_URL`, `CORS_ORIGINS`.
4. Use a Postgres that persists. Render's free Postgres expires after 30 days, so the Supabase database (free, persistent) is the recommended `DATABASE_URL`. Use its connection string in the Render dashboard only.
5. Free web services sleep after 15 minutes idle; the first request after that takes about 30 seconds.

## Endpoints

| Method | Path | Who |
|---|---|---|
| GET | `/health`, `/api/stacks`, `/api/topics` | public |
| GET / PATCH | `/api/me` | signed in |
| GET | `/api/feed?topic=&stack=&limit=&before=` | approved |
| GET / POST | `/api/content`, `/api/content/mine`, `/api/content/:id` | approved |
| PATCH / DELETE | `/api/content/:id` | author |
| GET | `/api/review/queue` | reviewer, admin |
| POST | `/api/review/:id` | reviewer, admin |
| GET / POST | `/api/programs`, `/api/programs/:slug` | approved |
| POST / DELETE | `/api/programs/:slug/items[/:id]` | plan owner |
| PUT | `/api/programs/:slug/items/:id/progress` | approved |
| GET / POST | `/api/admin/users`, `/api/admin/users/:id/access`, `/api/admin/users/:id/role` | admin |

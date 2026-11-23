-- Scored reviews (checklist: points met of total), batch-only feed items, peer comments, an opt-in leaderboard and email invites.
-- Idempotent: safe to run more than once.
alter table submission_reviews add column if not exists scores jsonb null;

-- A feed item can be for one batch only (the author's batch). Null = for everyone.
alter table content_items add column if not exists batch_id uuid null references batches(id) on delete set null;
create index if not exists content_items_batch_idx on content_items (batch_id) where batch_id is not null;

-- A person decides whether they appear on their batch's leaderboard. Off by default.
alter table app_users add column if not exists leaderboard_opt_in boolean not null default false;

-- People the admin invited by email: approved (and put in the batch) the first time they sign in.
create table if not exists invites (
  email      text primary key check (email = lower(email) and char_length(email) <= 200),
  batch_id   uuid null references batches(id) on delete set null,
  created_by uuid null references app_users(id) on delete set null,
  created_at timestamptz not null default now(),
  used_at    timestamptz null
);

-- A classmate's comment on a submitted piece of work (one per person per submission).
create table if not exists peer_comments (
  id            uuid primary key default gen_random_uuid(),
  submission_id uuid not null references submissions(id) on delete cascade,
  author_id     uuid not null references app_users(id) on update cascade on delete cascade,
  comment       text not null check (char_length(comment) between 1 and 1000),
  created_at    timestamptz not null default now(),
  unique (submission_id, author_id)
);

do $$
declare t text;
begin
  foreach t in array array['invites','peer_comments'] loop
    execute format('alter table %I enable row level security', t);
    if exists (select 1 from pg_roles where rolname = 'anon') then execute format('revoke all on %I from anon', t); end if;
    if exists (select 1 from pg_roles where rolname = 'authenticated') then execute format('revoke all on %I from authenticated', t); end if;
  end loop;
end $$;

-- Practice lab: runs a person chose to keep (the Day 1 and Day 2 practicals run from the web app), and one shared sample per day picked by an admin.
-- The result is the JSON the web app built; it is stored and returned as data only. No API keys are ever stored here (the API refuses a result that holds one).
-- Idempotent: safe to run more than once.
create table if not exists lab_runs (
  id         uuid primary key default gen_random_uuid(),
  user_id    uuid not null references app_users(id) on update cascade on delete cascade,
  day        smallint not null check (day between 1 and 3),
  label      text not null check (char_length(label) between 1 and 120),
  models     text[] not null default '{}' check (cardinality(models) <= 4),
  result     jsonb not null,
  is_sample  boolean not null default false,
  created_at timestamptz not null default now(),
  -- the API refuses results over 64 KB as sent; this is a backstop (stored jsonb text adds spaces, so it allows some room)
  constraint lab_runs_result_size check (octet_length(result::text) <= 131072)
);
create index if not exists lab_runs_user_idx on lab_runs (user_id, created_at desc);
-- At most one shared sample per day.
create unique index if not exists lab_runs_one_sample_per_day on lab_runs (day) where is_sample;

-- Same lock-down as 008: the table is for the API only, never for Supabase's public web API.
do $$
begin
  alter table public.lab_runs enable row level security;
  if exists (select 1 from pg_roles where rolname = 'anon') then revoke all on table public.lab_runs from anon; end if;
  if exists (select 1 from pg_roles where rolname = 'authenticated') then revoke all on table public.lab_runs from authenticated; end if;
end $$;

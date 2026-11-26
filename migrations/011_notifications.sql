-- In-app notifications: the 10:00 "you have not filled your daily log" reminder, and "someone submitted their week" for reviewers and the admin.
-- One row per person, kind, day and subject, so running the reminder job twice (a restart, a catch-up when the free server slept) never sends twice.
-- Idempotent: safe to run more than once.
create table if not exists tracker_notifications (
  id         uuid primary key default gen_random_uuid(),
  user_id    uuid not null references app_users(id) on update cascade on delete cascade,
  kind       text not null check (kind in ('daily_log_missed', 'week_submitted')),
  on_date    date not null,
  ref        text not null default '' check (char_length(ref) <= 80),
  title      text not null check (char_length(title) between 1 and 200),
  body       text not null default '' check (char_length(body) <= 1000),
  link       text null check (link is null or (link ~ '^/' and char_length(link) <= 300)),
  created_at timestamptz not null default now(),
  read_at    timestamptz null,
  emailed_at timestamptz null
);
create unique index if not exists tracker_notifications_once_idx on tracker_notifications (user_id, kind, on_date, ref);
create index if not exists tracker_notifications_user_time_idx on tracker_notifications (user_id, created_at desc);

alter table public.tracker_notifications enable row level security;
do $$
begin
  if exists (select 1 from pg_roles where rolname = 'anon') then revoke all on table public.tracker_notifications from anon; end if;
  if exists (select 1 from pg_roles where rolname = 'authenticated') then revoke all on table public.tracker_notifications from authenticated; end if;
end $$;

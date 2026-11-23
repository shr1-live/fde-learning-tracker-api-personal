-- Daily log: tasks as rows, days as columns. One row per person, day and task, with hours and a note.
-- A week is "submitted" once the person presses Submit (they can still edit; the time of the last submit is kept).
-- Idempotent: safe to run more than once.
create table if not exists daily_log (
  user_id    uuid not null references app_users(id) on update cascade on delete cascade,
  log_date   date not null,
  task       text not null check (char_length(task) between 1 and 120),
  hours      numeric(4,2) not null check (hours >= 0 and hours <= 24),
  note       text null check (note is null or char_length(note) <= 500),
  updated_at timestamptz not null default now(),
  primary key (user_id, log_date, task)
);
create index if not exists daily_log_date_idx on daily_log (log_date);

create table if not exists daily_log_weeks (
  user_id      uuid not null references app_users(id) on update cascade on delete cascade,
  week_start   date not null check (extract(isodow from week_start) = 1),
  submitted_at timestamptz not null default now(),
  primary key (user_id, week_start)
);

-- Same lock-down as 008: for the API only, never for Supabase's public web API.
do $$
declare t text;
begin
  foreach t in array array['daily_log','daily_log_weeks'] loop
    execute format('alter table %I enable row level security', t);
    if exists (select 1 from pg_roles where rolname = 'anon') then execute format('revoke all on %I from anon', t); end if;
    if exists (select 1 from pg_roles where rolname = 'authenticated') then execute format('revoke all on %I from authenticated', t); end if;
  end loop;
end $$;

-- RAG practice (Week 2): each person's approved documents for the document assistant. Chunks and embeddings are computed per question, so only the text is stored.
-- Idempotent: safe to run more than once.
create table if not exists rag_documents (
  id         uuid primary key default gen_random_uuid(),
  owner_id   uuid not null references app_users(id) on update cascade on delete cascade,
  title      text not null check (char_length(title) between 1 and 120),
  body       text not null check (char_length(body) between 1 and 20000),
  tags       text[] not null default '{}',
  created_at timestamptz not null default now(),
  unique (owner_id, title)
);

-- Same lock-down as 008: for the API only, never for Supabase's public web API.
do $$
begin
  alter table rag_documents enable row level security;
  if exists (select 1 from pg_roles where rolname = 'anon') then revoke all on rag_documents from anon; end if;
  if exists (select 1 from pg_roles where rolname = 'authenticated') then revoke all on rag_documents from authenticated; end if;
end $$;

-- Week 2 Day 3: vector search table for 384-number embeddings (BAAI/bge-small-en-v1.5).
-- The service creates this by itself on start; run it by hand in the Supabase SQL editor if you prefer to review it first.
-- A different embedding model with another size needs its own table (vector_chunks_<size>), never the same column.
create extension if not exists vector;   -- Supabase: or switch it on under Database > Extensions

create table if not exists vector_chunks_384 (
  id           text primary key,                        -- owner:doc_id:part
  owner        text not null default 'shared',
  doc_id       text not null,
  title        text not null,
  part         int  not null,
  content      text not null,
  metadata     jsonb not null default '{}',             -- department, region, year, ...
  embed_model  text not null,                           -- only vectors from the same model are compared
  embedding    vector(384) not null,
  tsv          tsvector generated always as (to_tsvector('english', content)) stored,
  created_at   timestamptz not null default now()
);

create index if not exists vector_chunks_384_hnsw      on vector_chunks_384 using hnsw (embedding vector_cosine_ops) with (m = 16, ef_construction = 64);
create index if not exists vector_chunks_384_tsv       on vector_chunks_384 using gin (tsv);
create index if not exists vector_chunks_384_meta      on vector_chunks_384 using gin (metadata jsonb_path_ops);
create index if not exists vector_chunks_384_owner_doc on vector_chunks_384 (owner, doc_id);

-- Locked down like the tracker's other tables: no policy, so the browser (anon and authenticated roles) cannot read or write it.
-- Only the service's own database connection (the table owner) can. Per-person policies are Day 5.
alter table vector_chunks_384 enable row level security;

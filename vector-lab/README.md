# Vector lab (Week 2, Day 3: vector search), Postgres + pgvector

Vector retrieval with metadata filters, built from LangChain parts. **This branch uses Postgres with pgvector (the primary store; Supabase works).**
The Chroma version (offline, no database server) is on the branch `feature/week2-day3-vector-chroma-local`. Switch with `git switch <branch>`.

| Part | What | Where |
|---|---|---|
| Vector database | **Postgres + pgvector**: HNSW index (cosine), generated full-text column, JSONB metadata, row security on | `app/engine.py`, `sql/001_vector_chunks_384.sql` |
| Embeddings | `BAAI/bge-small-en-v1.5` (384 numbers, quantized ONNX through fastembed, CPU, no key); model name stored on every row | `BgeEmbeddings` |
| Chunking | LangChain `RecursiveCharacterTextSplitter` (400 characters, overlap 60), stable chunk ids | `VectorLab.ingest` |
| Search | semantic (vector), keyword (Postgres full-text), hybrid (reciprocal rank fusion); Top-K; metadata filter as bound SQL parameters | `VectorLab.search`, `filter_sql` |
| Pipeline | LangGraph: plan (filter from the question) -> route by mode -> search -> evidence gate | `app/graph.py` |
| HTTP API | FastAPI: `/search`, `/documents`, `/stats` | `app/server.py` |
| Evaluation | 25 fixed questions, mode x K, filter effect, threshold, latency, index scale | `run_eval.py` |
| Tests | 48 tests on the real model and a real Postgres | `tests/` |

## Set up with Supabase

1. Dashboard > Database > Extensions: switch on **vector**.
2. Dashboard > Connect: copy the **Session pooler** (or Direct) connection string. Do not use the Transaction pooler (port 6543).
3. `copy .env.example .env` and put the string in `VECTOR_LAB_DB_URL`. The `.env` file is git-ignored; never commit or paste it.
4. Use a test project: the tests create and delete rows (they only touch tables named `vector_chunks_*`, `scale_t` and the owners `t-*`, `eval`, `scale`).

## Run

```
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python -m pytest tests            # 48 tests; first run downloads the 70 MB model
.venv\Scripts\python run_eval.py --scale        # writes results/eval_pgvector.json and .csv (scale test needs about 4 minutes)
$env:VECTOR_LAB_ALLOW_LOCAL = "1"               # only if you have no VECTOR_LAB_DB_URL: use the local embedded Postgres
.venv\Scripts\uvicorn app.server:app --port 8100
```

Without `VECTOR_LAB_DB_URL`, tests and evaluation start a local Postgres 16 with pgvector through the `pgserver` package (data in `data/pg`, no Docker).

```
curl -X POST localhost:8100/documents/sample
curl -X POST localhost:8100/search -H "content-type: application/json" \
  -d '{"question":"How many days of annual leave do I get?","mode":"hybrid","k":3,"filter":{"region":"Germany"}}'
```

Filters: `{"region": "UK"}`, `{"year": {"$gte": 2025}}`, `{"$and": [...]}`, `{"$or": [...]}`, `{"department": {"$in": ["hr","it"]}}`, `$ne`, `$gt`, `$lt`, `$lte`.
Set `VECTOR_LAB_KEY` to require an `X-Api-Key` header.

## Limits

- Documents are invented; results show how the methods compare here, not on real company documents.
- Keyword search is Postgres full-text ranking (`ts_rank_cd`), not BM25: it has no inverse-document-frequency weight, so its scores differ from the Chroma branch.
- Every query is scoped to one `owner`; real per-person policies and sign-in are Day 5.
- Not yet tested against a hosted Supabase project (only a local Postgres 16 with pgvector 0.6.2). Supabase has a newer pgvector (iterative index scans for filtered searches).

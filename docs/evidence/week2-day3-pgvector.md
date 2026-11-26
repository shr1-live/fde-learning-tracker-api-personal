# Week 2, Day 3 evidence: vector search on Postgres + pgvector

Same task, corpus (12 documents, 17 chunks) and 25 questions as [the Chroma version](week2-day3.md), now on **Postgres + pgvector** (the primary store). Code: [vector-lab](../../vector-lab/README.md), branch `feature/week2-day3-vector-pgvector`. The Chroma version stays on `feature/week2-day3-vector-chroma-local` as the offline fallback.

Tested on a local Postgres 16.2 with pgvector 0.6.2 (started by `pgserver`, no Docker). **Not yet tested on Supabase.**

## What is stored
One table `vector_chunks_384`: `embedding vector(384)` with an HNSW index (cosine, m 16, ef_construction 64), a generated `tsvector` column (GIN index) for keyword search, `metadata jsonb` (GIN) for filters, `owner`, and `embed_model` on every row. Row-level security is on with no policy (only the service connection can read it). SQL: `vector-lab/sql/001_vector_chunks_384.sql`.

## Tests: 48 pass
The 29 from the Chroma branch plus: one owner never sees or deletes another's documents (V13), vectors from another embedding model are never compared (V14), hostile filters are refused (V15), hostile questions are only data (V16), filter values are bound parameters (V17), the HNSW index and row security exist (V18). Also: searching does not re-embed stored passages (V08).

## Results (22 answerable questions, K=3, no filter)

| Mode | Chroma hit | pgvector hit | pgvector MRR |
|---|---|---|---|
| semantic | 95.5% | 95.5% | 0.871 |
| keyword | 95.5% | 95.5% | 0.848 |
| hybrid | 100% | **95.5%** | 0.886 |

- Semantic results are identical to Chroma at every K: same model, same cosine distance.
- **Hybrid is worse here (95.5% against 100%).** The keyword side is Postgres full-text ranking (`ts_rank_cd`), not BM25, so the fused list differs and one question's document falls out of the top 3. This is a keyword-ranking difference, not a pgvector fault. One question is 4.5 points, so it is a small sample. Not yet tuned.
- The metadata filter works the same: on the 6 filtered questions MRR goes from 0.694 to 1.000 and precision from 33.3% to 66.7% (semantic and hybrid).
- Search takes 38 to 87 ms (query embedding); keyword about 1 ms.
- The similarity gate has the same weakness: the ranges of answerable (lowest 0.586) and not-in-documents (highest 0.599) questions overlap.

## Index scale (generated passages, local CPU)

| Chunks | Index build | HNSW ef 40: ms / recall@10 | HNSW ef 100: ms / recall@10 | Exact scan ms |
|---|---|---|---|---|
| 1,000 | 0.7 s | 1.5 / 100% | 1.9 / 100% | 1.8 |
| 5,000 | 2.3 s | 2.7 / 98.0% | 4.4 / 99.6% | 4.3 |
| 10,000 | 6.3 s | 2.2 / 96.2% | 3.1 / 98.8% | 2.2 |

At this size the index is no faster than an exact scan and loses recall; a higher `ef_search` recovers it. Embedding 10,000 passages took 206 s, so embedding is the real cost.

## Finding: a selective filter breaks HNSW search (pgvector 0.6.2)
At 10,000 chunks, with the index in use, asking for 10 rows:

| Filter keeps | ef_search 40 | ef_search 400 |
|---|---|---|
| 10% of rows | 4.4 returned | 10 returned |
| 1% of rows | 0.4 returned | 3.4 returned |

The index finds its nearest candidates first and the filter removes most of them afterwards. Our 17-chunk corpus never shows this. Mitigations: raise `ef_search`; pgvector 0.8+ has iterative scans (the engine requests `hnsw.iterative_scan = relaxed_order`, ignored on older versions); or split by an indexed column. Check the Supabase version before relying on filtered search at scale.

## Not done
- Not run on Supabase (needs `VECTOR_LAB_DB_URL`); iterative scans untested.
- Hybrid ranking not tuned; no reranker.
- Per-person row-level policies and sign-in are Day 5; only owner scoping in code exists.
- The portal does not call this service, and the .NET assistant of Days 1 and 2 is not connected to it.
- One earlier evaluation run failed with an out-of-memory error from the local Postgres; it could not be reproduced and a re-run completed.

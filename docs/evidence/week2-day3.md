# Week 2, Day 3 evidence: vector search

**Task:** implement vector-based retrieval with metadata filters; test different Top-K values; compare semantic search with keyword or hybrid search.

**Acceptance criteria:** store and retrieve document embeddings; similarity-based retrieval; Top-K configuration; metadata filters applied correctly; a fixed set of representative questions; relevance observations and basic performance results.

## What was built ([vector-lab](../../vector-lab/README.md))

| Part | Detail |
|---|---|
| Vector database | **Chroma** (HNSW index, cosine distance, saved to disk). Docker was not available, so a server database (pgvector, Qdrant) was not used; only the store class would change. |
| Embeddings | `BAAI/bge-small-en-v1.5`, 384 numbers per passage, quantized ONNX through fastembed on CPU, wrapped as a LangChain `Embeddings` class. |
| Ingestion | LangChain `RecursiveCharacterTextSplitter` (400 characters, overlap 60). Chunk ids are `doc_id:part`, so a re-ingest replaces and a delete removes (test V07); the index survives a restart (V08). |
| Search | `semantic` (vector similarity), `keyword` (BM25), `hybrid` (reciprocal rank fusion); `k` 1 to 50; metadata filter (`$and`, `$or`, `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`). |
| Pipeline | LangGraph graph: plan (reads a region or year from the question) -> route by mode -> search -> evidence gate (similarity threshold). |
| API | FastAPI: `POST /search`, `POST/DELETE /documents`, `GET /stats`; optional `X-Api-Key`. |
| Tests | 29 pass on the real model and database ([tests](../../vector-lab/tests/test_vector_lab.py)): filter correctness for 3 modes x 6 filters (V03), the filter picks the right near-duplicate (V04), Top-K and ordering (V05), bad settings refused (V06), graph routing, gate and region extraction (V10, V11). |

**Corpus:** 12 invented handbook documents, 17 chunks, with metadata `department`, `region`, `year`. They include near-duplicates on purpose: annual leave in the UK (24 days), in Germany (30 days) and an archived 2024 UK policy (20 days); hotel caps in Europe and the US; remote work in general and in Germany. Ingest took 2.2 s.

**Question set:** 25 fixed questions: 10 plain, 6 paraphrased, 6 that need a filter, 3 not in the documents ([corpus.py](../../vector-lab/app/corpus.py)). Raw results: `vector-lab/results/eval.json` and `eval.csv`.

## Results: mode and Top-K (22 answerable questions, no filter)

Hit = a chunk of a correct document is in the top K. Precision = share of the K chunks from a correct document. Latency is the search call, warm, median per question.

| Mode | K | Hit | MRR | Precision | Median ms |
|---|---|---|---|---|---|
| semantic | 1 | 81.8% | 0.818 | 81.8% | 54.3 |
| semantic | 3 | 95.5% | 0.871 | 45.5% | 34.5 |
| semantic | 5 | 100% | 0.883 | 30.9% | 21.8 |
| keyword | 1 | 81.8% | 0.818 | 81.8% | 0.17 |
| keyword | 3 | 95.5% | 0.879 | 50.8% | 0.16 |
| keyword | 5 | 100% | 0.888 | 45.7% | 0.18 |
| hybrid | 1 | 81.8% | 0.818 | 81.8% | 26.1 |
| hybrid | 3 | **100%** | 0.886 | 45.5% | 32.1 |
| hybrid | 5 | 100% | 0.886 | 29.1% | 26.5 |

## Observations

1. **Hybrid at K 3 is the only setting that finds the right document for all 22 questions at K 3**; semantic and keyword each miss one (95.5%). It fixes the paraphrase group (83.3% for both single methods, 100% for hybrid).
2. **Keyword search is as good as vector search on this corpus.** It has the same hit rate as semantic at every K and is about 200 times faster, because the corpus is small and most questions share words with the documents. The expected gap on paraphrases only partly appeared: both single methods score 83.3% on them (one question missed each), and hybrid recovers all six.
3. **A larger K buys recall and costs precision.** K 1 is wrong for 4 questions out of 22 because near-duplicate documents compete for first place; K 5 never misses but only 29 to 46% of the passages are relevant, so more noise reaches the model.
4. **The metadata filter is what fixes the near-duplicates.** For the 6 questions that name a region or year, with and without the filter at K 3:

   | | Hit | MRR | Precision |
   |---|---|---|---|
   | No filter (hybrid) | 100% | 0.694 | 33.3% |
   | With filter (hybrid) | 100% | **1.000** | **66.7%** |

   Without the filter the right document is in the top 3 but often not first (the other country's policy or the archived 2024 one ranks above it). With it, it is first every time and the wrong-country and archived passages never appear (V03, V04).
5. **Search time is the query embedding.** Vector and hybrid searches take 20 to 55 ms, almost all of it embedding the question on a CPU; BM25 takes 0.17 ms.

## Not-in-the-documents questions (the evidence gate)

Top-1 cosine similarity: lowest for an answerable question 0.586; highest for a question not in the documents 0.599 ("How much is the annual bonus?", because of "annual"). The two ranges overlap, so no threshold separates them. Threshold 0.60 refuses all 3 unanswerable questions but also refuses 1 of 22 answerable ones ("When does a medical certificate become necessary?", 0.586). **A similarity threshold alone is not a reliable gate here**; the Day 1 answer rule (matched query terms in the passage) is still needed.

## Performance and scale (generated passages, same vectors for both methods)

| Chunks | Embedding time | Index build | Exact search (median) | HNSW (median / p95) | HNSW overlap with exact top 10 |
|---|---|---|---|---|---|
| 1,000 | 28.8 s | 0.9 s | 0.41 ms | 3.09 / 3.74 ms | 100% |
| 5,000 | 103.9 s | 4.2 s | 1.00 ms | 2.49 / 3.05 ms | 99.8% |
| 10,000 | 190.2 s | 7.8 s | 2.36 ms | 3.37 / 4.40 ms | 98.4% |

At up to 10,000 chunks exact search is as fast or faster than the HNSW index, so the index brings no speed gain yet (its time barely grows, exact search's does); the approximation costs about 1.6% of the true top 10 at 10,000. **Embedding is the bottleneck at about 19 ms per passage**: ingesting 10,000 chunks takes over 3 minutes, so embeddings must be stored and reused, and only changed documents re-embedded. These times are for this laptop CPU.

## What is not done

- The documents are 12 invented ones (17 chunks); the question set is 25. Results show how the methods compare here, not on real company documents.
- No reranker, no similarity threshold that works, and no approximate-index tuning (`M`, `ef`) was tried.
- Metadata filtering in keyword search is done in Python over an in-memory index, not inside the database.
- Chroma is an embedded database for one process. Not tested: pgvector or Qdrant, concurrent users, or the service deployed. The portal does not call this service yet; the tracker's own database is unchanged (Postgres).
- Not connected to the .NET document assistant of Days 1 and 2, which still uses its own hashed embeddings.

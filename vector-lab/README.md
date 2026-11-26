# Vector lab (Week 2, Day 3: vector search)

Vector retrieval with metadata filters, built from LangChain parts and a real vector database.

| Part | What | Where |
|---|---|---|
| Vector database | **Chroma**, HNSW index, cosine distance, saved to disk | `app/engine.py` (`VectorLab`) |
| Embeddings | `BAAI/bge-small-en-v1.5` (384 numbers, quantized ONNX through fastembed, runs on CPU, no key) | `BgeEmbeddings` |
| Chunking | LangChain `RecursiveCharacterTextSplitter` (400 characters, overlap 60), stable chunk ids | `VectorLab.ingest` |
| Search | semantic (vector), keyword (BM25), hybrid (reciprocal rank fusion); Top-K; metadata filter | `VectorLab.search` |
| Pipeline | LangGraph: plan (filter from the question) -> route by mode -> search -> evidence gate | `app/graph.py` |
| HTTP API | FastAPI: `/search`, `/documents`, `/stats` | `app/server.py` |
| Evaluation | 25 fixed questions, mode x K, filter effect, threshold, latency, scale | `run_eval.py` |
| Tests | 29 tests on the real model and database | `tests/` |

## Run

```
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python -m pytest tests            # 29 tests, about 35 s the first time (downloads the model, 70 MB)
.venv\Scripts\python run_eval.py --scale        # writes results/eval.json and results/eval.csv
.venv\Scripts\uvicorn app.server:app --port 8100
```

```
curl -X POST localhost:8100/documents/sample
curl -X POST localhost:8100/search -H "content-type: application/json" \
  -d '{"question":"How many days of annual leave do I get?","mode":"hybrid","k":3,"filter":{"region":"Germany"}}'
```

Filters use the Chroma syntax: `{"region": "UK"}`, `{"year": {"$gte": 2025}}`, `{"$and": [...]}`, `{"$or": [...]}`, `{"department": {"$in": ["hr","it"]}}`.
Set `VECTOR_LAB_KEY` to require an `X-Api-Key` header.

## Moving to another database

Only `VectorLab.store` and the four calls around it (`add_documents`, `get`, `delete`, `similarity_search_with_score`) are Chroma-specific.
For pgvector use `langchain-postgres` `PGVector`, for Qdrant use `langchain-qdrant`; the search modes, filter checks, graph and evaluation stay.

## Limits

- Chroma is an embedded database for one process. It is not a multi-user server.
- The keyword index (BM25) is rebuilt in memory after every change; fine for thousands of chunks, not millions.
- The documents are invented. Results show how the methods compare here, not how they would do on real company documents.

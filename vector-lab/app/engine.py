"""Vector retrieval for Week 2, Day 3 on **Postgres + pgvector** (the primary store; the Chroma version is on the branch
feature/week2-day3-vector-chroma-local).

* Store: one Postgres table (`vector_chunks_<dimensions>`) with a pgvector column, an HNSW index (cosine), a generated
  full-text column and a JSONB metadata column. Works on Supabase, or any Postgres with the `vector` extension.
* Embeddings: BAAI/bge-small-en-v1.5 (384 numbers) through fastembed, as a LangChain Embeddings class. The model name is stored
  on every row and a search only compares vectors made by the same model.
* Chunking: LangChain RecursiveCharacterTextSplitter; chunk ids are doc_id:part, so a re-ingest replaces, never duplicates.
* Search: semantic (vector), keyword (Postgres full-text), hybrid (reciprocal rank fusion), with a metadata filter and Top-K.
* Isolation: every query is scoped to an `owner`, like the tracker's other tables.
"""
from __future__ import annotations

import copy
import re
import time
from dataclasses import dataclass

import numpy as np
from langchain_core.documents import Document
from langchain_core.embeddings import Embeddings
from langchain_text_splitters import RecursiveCharacterTextSplitter
from pgvector.psycopg import register_vector
from psycopg.types.json import Jsonb
from psycopg_pool import ConnectionPool

MODEL = "BAAI/bge-small-en-v1.5"
DIMENSIONS = 384
QUERY_PREFIX = "Represent this sentence for searching relevant passages: "  # bge: queries get this prefix, passages do not
MODES = ("semantic", "keyword", "hybrid")


class BgeEmbeddings(Embeddings):
    """fastembed (ONNX, runs on CPU, no GPU or API key) as a LangChain Embeddings object."""

    model_name = MODEL
    dimensions = DIMENSIONS

    def __init__(self, model: str = MODEL):
        from fastembed import TextEmbedding
        self._m = TextEmbedding(model_name=model)
        self.calls = 0  # passages embedded, so a test can prove vectors are reused

    def embed_documents(self, texts: list[str]) -> list[list[float]]:
        self.calls += len(texts)
        return [v.tolist() for v in self._m.embed(texts)]

    def embed_query(self, text: str) -> list[float]:
        return next(iter(self._m.embed([QUERY_PREFIX + text]))).tolist()


_WORD = re.compile(r"[a-z0-9]+(?:,[0-9]{3})*")
_KEY = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,40}$")


@dataclass
class Hit:
    chunk_id: str
    doc_id: str
    title: str
    text: str
    metadata: dict
    score: float  # semantic: cosine similarity 0..1; keyword: ts_rank_cd; hybrid: RRF
    rank: int = 0
    sem: float | None = None  # cosine similarity, when the passage was found by vector search


@dataclass
class Timing:
    embed_ms: float = 0.0
    search_ms: float = 0.0
    total_ms: float = 0.0


def matches(meta: dict, flt: dict | None) -> bool:
    """Python check of a filter ($and, $or, $eq, $ne, $gt, $gte, $lt, $lte, $in). Used by the tests to verify what the database returned."""
    if not flt:
        return True
    for key, cond in flt.items():
        if key == "$and":
            if not all(matches(meta, c) for c in cond):
                return False
        elif key == "$or":
            if not any(matches(meta, c) for c in cond):
                return False
        else:
            v = meta.get(key)
            if not isinstance(cond, dict):
                cond = {"$eq": cond}
            for op, want in cond.items():
                ok = {"$eq": lambda: v == want, "$ne": lambda: v != want, "$gt": lambda: v is not None and v > want,
                      "$gte": lambda: v is not None and v >= want, "$lt": lambda: v is not None and v < want,
                      "$lte": lambda: v is not None and v <= want, "$in": lambda: v in want}.get(op)
                if ok is None:
                    raise ValueError(f"Unknown filter operator {op}")
                if not ok():
                    return False
    return True


def filter_sql(flt: dict | None, params: list) -> str:
    """Turns a filter into a parameterised SQL condition on the JSONB `metadata` column. Keys are checked against a pattern and
    every value is a bound parameter, so a filter can never change the shape of the query."""
    if not flt:
        return "true"
    parts = []
    for key, cond in flt.items():
        if key in ("$and", "$or"):
            if not isinstance(cond, list) or not cond:
                raise ValueError(f"{key} needs a list of conditions")
            parts.append("(" + f" {key[1:]} ".join(filter_sql(c, params) for c in cond) + ")")
            continue
        if not _KEY.match(key):
            raise ValueError(f"Bad filter field name: {key!r}")
        if not isinstance(cond, dict):
            cond = {"$eq": cond}
        for op, want in cond.items():
            if op == "$eq":
                params.append(Jsonb({key: want})); parts.append("metadata @> %s")
            elif op == "$in":
                if not isinstance(want, list) or not want:
                    raise ValueError("$in needs a non-empty list")
                alts = []
                for w in want:
                    params.append(Jsonb({key: w})); alts.append("metadata @> %s")
                parts.append("(" + " or ".join(alts) + ")")
            elif op == "$ne":
                params.extend([key, Jsonb(want)]); parts.append("(metadata -> %s::text) is distinct from %s")
            elif op in ("$gt", "$gte", "$lt", "$lte"):
                if isinstance(want, bool) or not isinstance(want, (int, float, str)):
                    raise ValueError(f"{op} needs a number or text")
                sym = {"$gt": ">", "$gte": ">=", "$lt": "<", "$lte": "<="}[op]
                params.extend([key, Jsonb(want)]); parts.append(f"(metadata -> %s::text) {sym} %s")
            else:
                raise ValueError(f"Unknown filter operator {op}")
    return "(" + " and ".join(parts) + ")" if parts else "true"


class VectorLab:
    def __init__(self, url: str, embeddings: Embeddings | None = None, owner: str = "shared", chunk_size: int = 400, overlap: int = 60,
                 ef_search: int = 100, table: str | None = None):
        self.emb = embeddings or BgeEmbeddings()
        self.dim = int(getattr(self.emb, "dimensions", DIMENSIONS))
        self.model = getattr(self.emb, "model_name", MODEL)
        self.table = table or f"vector_chunks_{self.dim}"
        if not re.fullmatch(r"[a-z0-9_]{3,63}", self.table):
            raise ValueError("bad table name")
        self.owner, self.ef_search = owner, ef_search
        self.splitter = RecursiveCharacterTextSplitter(chunk_size=chunk_size, chunk_overlap=overlap)
        self._bootstrap(url)
        self.pool = ConnectionPool(url, min_size=1, max_size=4, configure=register_vector, kwargs={"autocommit": True}, open=True)
        self._create_table()

    def _bootstrap(self, url: str) -> None:
        import psycopg
        with psycopg.connect(url, autocommit=True) as c:  # the vector type must exist before connections register it
            c.execute("create extension if not exists vector")

    def _create_table(self) -> None:
        t = self.table
        with self.pool.connection() as c:
            c.execute(f"""create table if not exists {t} (
                id text primary key, owner text not null default 'shared', doc_id text not null, title text not null, part int not null,
                content text not null, metadata jsonb not null default '{{}}', embed_model text not null,
                embedding vector({self.dim}) not null,
                tsv tsvector generated always as (to_tsvector('english', content)) stored, created_at timestamptz not null default now())""")
            c.execute(f"create index if not exists {t}_hnsw on {t} using hnsw (embedding vector_cosine_ops) with (m = 16, ef_construction = 64)")
            c.execute(f"create index if not exists {t}_tsv on {t} using gin (tsv)")
            c.execute(f"create index if not exists {t}_meta on {t} using gin (metadata jsonb_path_ops)")
            c.execute(f"create index if not exists {t}_owner_doc on {t} (owner, doc_id)")
            c.execute(f"alter table {t} enable row level security")  # no policy: only this service's own connection can read or write

    def for_owner(self, owner: str) -> "VectorLab":
        """The same store (one connection pool, one embedding model) acting for another owner. Every query is scoped to `owner`."""
        clone = copy.copy(self)
        clone.owner = owner
        return clone

    def close(self) -> None:
        self.pool.close()

    # ---- ingestion, update, delete (kept in sync) ----
    def ingest(self, doc_id: str, title: str, body: str, **meta) -> int:
        """Split, embed and store one document in one transaction. Re-ingesting a doc_id replaces its chunks."""
        parts = self.splitter.split_text(body)
        docs = [Document(page_content=p, metadata={**meta, "part": i + 1}) for i, p in enumerate(parts)]
        vecs = self.emb.embed_documents([d.page_content for d in docs]) if docs else []
        with self.pool.connection() as c, c.transaction():
            c.execute(f"delete from {self.table} where owner = %s and doc_id = %s", (self.owner, doc_id))
            for i, (d, v) in enumerate(zip(docs, vecs)):
                m = {k: val for k, val in d.metadata.items() if k != "part"}
                c.execute(f"insert into {self.table} (id, owner, doc_id, title, part, content, metadata, embed_model, embedding) values (%s,%s,%s,%s,%s,%s,%s,%s,%s)",
                          (f"{self.owner}:{doc_id}:{i + 1}", self.owner, doc_id, title, i + 1, d.page_content, Jsonb(m), self.model, np.array(v, dtype=np.float32)))
        return len(docs)

    def delete(self, doc_id: str) -> int:
        with self.pool.connection() as c:
            return c.execute(f"delete from {self.table} where owner = %s and doc_id = %s", (self.owner, doc_id)).rowcount

    def count(self) -> int:
        with self.pool.connection() as c:
            return c.execute(f"select count(*) from {self.table} where owner = %s", (self.owner,)).fetchone()[0]

    def documents(self) -> list[dict]:
        with self.pool.connection() as c:
            rows = c.execute(f"select doc_id, title, metadata, count(*) from {self.table} where owner = %s group by doc_id, title, metadata order by doc_id", (self.owner,)).fetchall()
        return [{"doc_id": d, "title": t, **m, "chunks": n} for d, t, m, n in rows]

    # ---- search ----
    def _hit(self, row, rank: int, sem: float | None) -> Hit:
        cid, doc_id, title, part, text, meta, score = row
        return Hit(cid, doc_id, title, text, {**meta, "doc_id": doc_id, "title": title, "part": part}, round(float(score), 4), rank, sem)

    def _semantic(self, q: str, k: int, flt: dict | None) -> list[Hit]:
        params: list = []
        cond = filter_sql(flt, params)
        qv = np.array(self.emb.embed_query(q), dtype=np.float32)
        sql = (f"select id, doc_id, title, part, content, metadata, 1 - (embedding <=> %s) as sim from {self.table} "
               f"where owner = %s and embed_model = %s and {cond} order by embedding <=> %s limit %s")
        with self.pool.connection() as c, c.transaction():
            c.execute(f"set local hnsw.ef_search = {int(self.ef_search)}")
            try:
                with c.transaction():  # a savepoint: an unknown setting (pgvector before 0.8) must not abort the search
                    c.execute("set local hnsw.iterative_scan = relaxed_order")  # pgvector 0.8+: keep scanning when a filter removes candidates
            except Exception:
                pass
            rows = c.execute(sql, [qv, self.owner, self.model, *params, qv, k]).fetchall()
        return [self._hit(r, i + 1, round(float(r[6]), 4)) for i, r in enumerate(rows)]

    def _keyword(self, q: str, k: int, flt: dict | None) -> list[Hit]:
        words = [w for w in _WORD.findall(q.lower()) if w != "or"]
        if not words:
            return []
        params: list = []
        cond = filter_sql(flt, params)
        sql = (f"select id, doc_id, title, part, content, metadata, ts_rank_cd(tsv, query) as r from {self.table}, websearch_to_tsquery('english', %s) query "
               f"where owner = %s and tsv @@ query and {cond} order by r desc, id limit %s")
        with self.pool.connection() as c:
            rows = c.execute(sql, [" or ".join(words), self.owner, *params, k]).fetchall()
        return [self._hit(r, i + 1, None) for i, r in enumerate(rows)]

    def search(self, question: str, mode: str = "hybrid", k: int = 3, flt: dict | None = None) -> tuple[list[Hit], Timing]:
        if mode not in MODES:
            raise ValueError(f"mode must be one of {MODES}")
        if not 1 <= k <= 50:
            raise ValueError("k must be between 1 and 50")
        t0 = time.perf_counter()
        deep = max(k * 4, 20)
        if mode == "semantic":
            hits = self._semantic(question, k, flt)
        elif mode == "keyword":
            hits = self._keyword(question, k, flt)
        else:  # hybrid: reciprocal rank fusion of the two ranked lists
            sem, kw = self._semantic(question, deep, flt), self._keyword(question, deep, flt)
            fused: dict[str, float] = {}
            by_id: dict[str, Hit] = {}
            for lst in (sem, kw):
                for h in lst:
                    fused[h.chunk_id] = fused.get(h.chunk_id, 0.0) + 1.0 / (60 + h.rank)
                    by_id.setdefault(h.chunk_id, h)
            for h in sem:
                by_id[h.chunk_id].sem = h.sem
            order = sorted(fused, key=lambda c: -fused[c])[:k]
            hits = [Hit(c, by_id[c].doc_id, by_id[c].title, by_id[c].text, by_id[c].metadata, round(fused[c], 5), r + 1, by_id[c].sem) for r, c in enumerate(order)]
        ms = (time.perf_counter() - t0) * 1000
        return hits, Timing(search_ms=round(ms, 2), total_ms=round(ms, 2))

    def similarity(self, question: str, text: str) -> float:
        """Cosine similarity between a question and one passage (vectors are unit length, so a dot product)."""
        q, d = self.emb.embed_query(question), self.emb.embed_documents([text])[0]
        return round(sum(a * b for a, b in zip(q, d)), 4)

    def stats(self) -> dict:
        with self.pool.connection() as c:
            ver = c.execute("select extversion from pg_extension where extname = 'vector'").fetchone()[0]
            size = c.execute(f"select pg_size_pretty(pg_total_relation_size('{self.table}'))").fetchone()[0]
        return {"vector_db": "Postgres + pgvector", "pgvector_version": ver, "table": self.table, "index": "HNSW (m 16, ef_construction 64)", "ef_search": self.ef_search,
                "distance": "cosine", "embedding_model": self.model, "dimensions": self.dim, "chunks": self.count(), "documents": len(self.documents()), "table_size": size}

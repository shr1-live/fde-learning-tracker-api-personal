"""Vector retrieval for Week 2, Day 3, built from LangChain parts.

* Store: Chroma (a real vector database; HNSW index, cosine distance, saved to disk, metadata filters).
* Embeddings: BAAI/bge-small-en-v1.5 (384 numbers per passage) through fastembed, wrapped as a LangChain Embeddings class.
* Chunking: LangChain RecursiveCharacterTextSplitter; every chunk has a stable id (doc_id:part) so a re-ingest replaces, never duplicates.
* Search: semantic (vector), keyword (BM25) or hybrid (reciprocal rank fusion), all with the same metadata filter and Top-K.
"""
from __future__ import annotations

import math
import re
import time
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from langchain_chroma import Chroma
from langchain_core.documents import Document
from langchain_core.embeddings import Embeddings
from langchain_text_splitters import RecursiveCharacterTextSplitter

MODEL = "BAAI/bge-small-en-v1.5"
QUERY_PREFIX = "Represent this sentence for searching relevant passages: "  # bge: queries get this prefix, passages do not
MODES = ("semantic", "keyword", "hybrid")


class BgeEmbeddings(Embeddings):
    """fastembed (ONNX, runs on CPU, no GPU or API key) as a LangChain Embeddings object."""

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
_STOP = set("a an the of to in on at is are was were be been do does did how what when who which i my me we you it its for and or with from by this that can will".split())


def tokens(text: str) -> list[str]:
    return [w for w in _WORD.findall(text.lower()) if w not in _STOP]


@dataclass
class Hit:
    chunk_id: str
    doc_id: str
    title: str
    text: str
    metadata: dict
    score: float  # semantic: cosine similarity 0..1; keyword: BM25; hybrid: RRF
    rank: int = 0
    sem: float | None = None  # cosine similarity, when the passage was found by vector search


@dataclass
class Timing:
    embed_ms: float = 0.0
    search_ms: float = 0.0
    total_ms: float = 0.0


def matches(meta: dict, flt: dict | None) -> bool:
    """Chroma-style filter ($and, $or, $eq, $ne, $gt, $gte, $lt, $lte, $in) evaluated in Python, used by keyword search."""
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


class VectorLab:
    def __init__(self, path: str | Path | None = None, embeddings: Embeddings | None = None, collection: str = "handbook",
                 chunk_size: int = 400, overlap: int = 60):
        self.emb = embeddings or BgeEmbeddings()
        self.store = Chroma(collection_name=collection, embedding_function=self.emb, persist_directory=str(path) if path else None,
                            collection_metadata={"hnsw:space": "cosine"})
        self.splitter = RecursiveCharacterTextSplitter(chunk_size=chunk_size, chunk_overlap=overlap)
        self._bm25: dict | None = None  # built lazily, dropped on every change

    # ---- ingestion, update, delete (kept in sync) ----
    def ingest(self, doc_id: str, title: str, body: str, **meta) -> int:
        """Split, embed and store one document. Re-ingesting a doc_id replaces its chunks (parts that no longer exist are removed)."""
        self.delete(doc_id)
        parts = self.splitter.split_text(body)
        base = {"doc_id": doc_id, "title": title, **meta}
        docs = [Document(page_content=p, metadata={**base, "part": i + 1}) for i, p in enumerate(parts)]
        if docs:
            self.store.add_documents(docs, ids=[f"{doc_id}:{i + 1}" for i in range(len(docs))])
        self._bm25 = None
        return len(docs)

    def delete(self, doc_id: str) -> int:
        got = self.store.get(where={"doc_id": doc_id})
        ids = got.get("ids", [])
        if ids:
            self.store.delete(ids=ids)
        self._bm25 = None
        return len(ids)

    def count(self) -> int:
        return self.store._collection.count()

    def documents(self) -> list[dict]:
        got = self.store.get(include=["metadatas"])
        seen: dict[str, dict] = {}
        for m in got["metadatas"]:
            d = seen.setdefault(m["doc_id"], {**{k: v for k, v in m.items() if k != "part"}, "chunks": 0})
            d["chunks"] += 1
        return sorted(seen.values(), key=lambda d: d["doc_id"])

    # ---- search ----
    def _semantic(self, q: str, k: int, flt: dict | None) -> list[Hit]:
        res = self.store.similarity_search_with_score(q, k=k, filter=flt or None)  # score = cosine distance
        return [Hit(d.id or f"{d.metadata['doc_id']}:{d.metadata['part']}", d.metadata["doc_id"], d.metadata["title"], d.page_content, d.metadata,
                    round(1.0 - dist, 4), i + 1, round(1.0 - dist, 4)) for i, (d, dist) in enumerate(res)]

    def _index(self) -> dict:
        if self._bm25 is None:
            got = self.store.get(include=["documents", "metadatas"])
            toks = [tokens(t) for t in got["documents"]]
            df: Counter = Counter()
            for t in toks:
                df.update(set(t))
            self._bm25 = {"ids": got["ids"], "texts": got["documents"], "meta": got["metadatas"], "toks": toks, "df": df,
                          "avg": (sum(map(len, toks)) / len(toks)) if toks else 0.0}
        return self._bm25

    def _keyword(self, q: str, k: int, flt: dict | None, k1: float = 1.5, b: float = 0.75) -> list[Hit]:
        ix = self._index()
        n, qt = len(ix["ids"]), tokens(q)
        scored = []
        for i, t in enumerate(ix["toks"]):
            if not matches(ix["meta"][i], flt):
                continue
            tf = Counter(t)
            s = 0.0
            for w in set(qt):
                if tf[w]:
                    idf = math.log(1 + (n - ix["df"][w] + 0.5) / (ix["df"][w] + 0.5))
                    s += idf * tf[w] * (k1 + 1) / (tf[w] + k1 * (1 - b + b * len(t) / (ix["avg"] or 1)))
            if s > 0:
                scored.append((s, i))
        scored.sort(key=lambda x: -x[0])
        m = ix["meta"]
        return [Hit(ix["ids"][i], m[i]["doc_id"], m[i]["title"], ix["texts"][i], m[i], round(s, 4), r + 1) for r, (s, i) in enumerate(scored[:k])]

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
        col = self.store._collection
        return {"vector_db": "Chroma", "index": "HNSW", "distance": "cosine", "embedding_model": MODEL, "dimensions": 384,
                "chunks": col.count(), "documents": len(self.documents())}

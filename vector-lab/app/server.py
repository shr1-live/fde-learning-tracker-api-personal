"""A small HTTP API over the vector lab:  uvicorn app.server:app --port 8100

Set VECTOR_LAB_KEY to require the header  X-Api-Key.  The database is VECTOR_LAB_DB_URL (Supabase or any Postgres with pgvector).
"""
from __future__ import annotations

import os

from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel, Field

from .corpus import DOCS
from .db import database_url
from .engine import MODES, VectorLab
from .graph import build, run

app = FastAPI(title="FDE vector lab", version="1.0")
_lab: VectorLab | None = None
_graph = None


def lab() -> VectorLab:
    global _lab, _graph
    if _lab is None:
        _lab = VectorLab(database_url(allow_local=os.environ.get("VECTOR_LAB_ALLOW_LOCAL") == "1"))
        _graph = build(_lab)
    return _lab


def auth(x_api_key: str | None = Header(default=None)):
    want = os.environ.get("VECTOR_LAB_KEY")
    if want and x_api_key != want:
        raise HTTPException(401, "Missing or wrong X-Api-Key.")


class SearchIn(BaseModel):
    question: str = Field(min_length=2, max_length=500)
    mode: str = "hybrid"
    k: int = Field(default=3, ge=1, le=50)
    filter: dict | None = None
    auto_filter: bool = False
    min_score: float = Field(default=0.0, ge=0.0, le=1.0)


class DocIn(BaseModel):
    doc_id: str = Field(pattern=r"^[a-z0-9-]{3,60}$")
    title: str = Field(min_length=2, max_length=120)
    body: str = Field(min_length=20, max_length=20000)
    department: str = "general"
    region: str = "All"
    year: int = Field(default=2025, ge=1990, le=2100)


@app.get("/health")
def health():
    return {"ok": True}


@app.get("/stats", dependencies=[Depends(auth)])
def stats():
    return lab().stats()


@app.get("/documents", dependencies=[Depends(auth)])
def documents():
    return lab().documents()


@app.post("/documents/sample", dependencies=[Depends(auth)])
def load_sample():
    n = sum(lab().ingest(d.doc_id, d.title, d.body, department=d.department, region=d.region, year=d.year) for d in DOCS)
    return {"documents": len(DOCS), "chunks": n}


@app.post("/documents", dependencies=[Depends(auth)])
def add(doc: DocIn):
    return {"chunks": lab().ingest(doc.doc_id, doc.title, doc.body, department=doc.department, region=doc.region, year=doc.year)}


@app.delete("/documents/{doc_id}", dependencies=[Depends(auth)])
def remove(doc_id: str):
    n = lab().delete(doc_id)
    if not n:
        raise HTTPException(404, "No such document.")
    return {"removed_chunks": n}


@app.post("/search", dependencies=[Depends(auth)])
def search(body: SearchIn):
    if body.mode not in MODES:
        raise HTTPException(400, f"mode must be one of {list(MODES)}")
    lab()
    try:
        s = run(_graph, body.question, body.mode, body.k, body.filter, body.min_score, body.auto_filter)
    except ValueError as e:
        raise HTTPException(400, str(e))
    return {"enough_evidence": s["enough_evidence"], "filter": s["applied_filter"], "timing_ms": s["timing"].search_ms, "trace": s["trace"],
            "hits": [{"rank": h.rank, "doc_id": h.doc_id, "title": h.title, "score": h.score, "similarity": h.sem, "metadata": h.metadata, "text": h.text} for h in s["hits"]]}

"""A small HTTP API over the vector lab:  uvicorn app.server:app --port 8100

Set VECTOR_LAB_KEY to require the header  X-Api-Key.  The database is VECTOR_LAB_DB_URL (Supabase or any Postgres with pgvector).
"""
from __future__ import annotations

import base64
import binascii
import hmac
import os
from pathlib import Path

from fastapi import Depends, FastAPI, Header, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from .assistant import MAX_BYTES, Assistant, AssistantError, Caller, RequestLog, load_users, new_request_id
from .corpus import DOCS
from .db import ROOT, database_url
from .engine import MODES, VectorLab
from .graph import build, run

app = FastAPI(title="FDE vector lab", version="1.0")

# The portal page calls this service straight from the browser. Allowed origins: the local dev and preview servers and the live portal;
# add more with VECTOR_LAB_CORS (comma separated). No cookies are used, so credentials stay off.
_ORIGINS = ["http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:4173", "http://127.0.0.1:4173", "https://fde-learniing-tracker.vercel.app",
            *[o.strip() for o in os.environ.get("VECTOR_LAB_CORS", "").split(",") if o.strip()]]
app.add_middleware(CORSMiddleware, allow_origins=_ORIGINS, allow_methods=["GET", "POST", "DELETE"], allow_headers=["content-type", "x-api-key"], allow_credentials=False, max_age=600)
_lab: VectorLab | None = None
_graph = None
_assistant: Assistant | None = None


@app.middleware("http")
async def request_id(request: Request, call_next):
    """Every request gets an id, returned in the X-Request-Id header and written to the log, so one call can be traced end to end."""
    request.state.request_id = new_request_id()
    response = await call_next(request)
    response.headers["X-Request-Id"] = request.state.request_id
    return response


@app.exception_handler(AssistantError)
async def assistant_error(request: Request, e: AssistantError):
    return JSONResponse({"error": {"code": e.code, "message": e.message, "request_id": getattr(request.state, "request_id", "")}}, status_code=e.status)


def assistant() -> Assistant:
    global _assistant
    if _assistant is None:
        _assistant = Assistant(lab(), RequestLog(ROOT / "data" / "requests.jsonl"))
    return _assistant


def identify(x_api_key: str | None = Header(default=None)) -> Caller:
    """Who is calling. With VECTOR_LAB_USERS set, the key must belong to a user (constant-time comparison). Without it, the service runs in
    single-user local mode (optionally protected by VECTOR_LAB_KEY)."""
    users = load_users()
    if users is None:
        want = os.environ.get("VECTOR_LAB_KEY")
        if want and not hmac.compare_digest((x_api_key or "").encode(), want.encode()):
            raise AssistantError(401, "unauthorized", "Missing or wrong X-Api-Key.")
        return Caller("local", (), admin=True)
    for key, caller in users.items():
        if hmac.compare_digest((x_api_key or "").encode(), key.encode()):
            return caller
    raise AssistantError(401, "unauthorized", "Missing or wrong X-Api-Key.")


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


# ---------------------------------------------------------------- Day 5: the document assistant
class UploadIn(BaseModel):
    filename: str = Field(min_length=3, max_length=200)
    content_base64: str = Field(min_length=4)
    department: str | None = None
    region: str = Field(default="All", max_length=30)
    year: int = Field(default=2025, ge=1990, le=2100)
    visibility: str = "department"


class Turn(BaseModel):
    role: str
    content: str = Field(max_length=2000)


class AskIn(BaseModel):
    question: str = Field(min_length=2, max_length=500)
    history: list[Turn] = Field(default_factory=list, max_length=20)
    k: int = Field(default=3, ge=1, le=10)
    mode: str = "hybrid"
    min_score: float = Field(default=0.60, ge=0.0, le=1.0)
    filter: dict | None = None


@app.get("/assistant/me")
def me(caller: Caller = Depends(identify)):
    return {"owner": caller.owner, "departments": list(caller.departments), "admin": caller.admin}


@app.post("/assistant/upload")
def upload(body: UploadIn, request: Request, caller: Caller = Depends(identify)):
    if len(body.content_base64) > MAX_BYTES * 4 // 3 + 16:
        raise AssistantError(413, "too_large", f"The file is larger than {MAX_BYTES // 1_000_000} MB.")
    try:
        data = base64.b64decode(body.content_base64, validate=True)
    except (binascii.Error, ValueError):
        raise AssistantError(400, "bad_encoding", "content_base64 is not valid base64.")
    return assistant().upload(caller, body.filename, data, body.department, body.region, body.year, body.visibility, request.state.request_id)


@app.post("/assistant/sample")
def assistant_sample(request: Request, caller: Caller = Depends(identify)):
    if not caller.admin:
        raise AssistantError(403, "admin_only", "Only an admin can load the sample documents.")
    lab_ = assistant().lab.for_owner(caller.owner)
    n = sum(lab_.ingest(d.doc_id, d.title, d.body, department=d.department, region=d.region, year=d.year, visibility="department", flagged=False, source_file=d.doc_id)
            for d in DOCS)
    assistant().log.record(request_id=request.state.request_id, owner=caller.owner, endpoint="sample", outcome="ok", chunks=n)
    return {"documents": len(DOCS), "chunks": n}


@app.get("/assistant/documents")
def assistant_documents(caller: Caller = Depends(identify)):
    return assistant().documents(caller)


@app.delete("/assistant/documents/{doc_id}")
def assistant_delete(doc_id: str, request: Request, caller: Caller = Depends(identify)):
    return {"removed_chunks": assistant().delete(caller, doc_id, request.state.request_id)}


@app.post("/assistant/ask")
def assistant_ask(body: AskIn, request: Request, caller: Caller = Depends(identify)):
    return assistant().ask(caller, body.question, [t.model_dump() for t in body.history], body.k, body.mode, body.min_score, body.filter, request.state.request_id)


@app.get("/assistant/log")
def assistant_log(n: int = 20, caller: Caller = Depends(identify)):
    return assistant().log.recent(caller.owner, max(1, min(n, 100)))

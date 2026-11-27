"""Week 2, Day 5: the document assistant. Upload approved documents, ask questions, get answers with citations, and a refusal when the
documents do not say. Built on the Day 3 store (Postgres + pgvector) and the Day 4 pipeline.

Stages, each in its own function so each can be tested alone:
  upload   check the file, extract text (txt, md, pdf, docx), clean it, screen it for hidden instructions, chunk, embed, store
  ask      follow-up rewrite, access filter, retrieve, evidence gate, generate with citations, verify, log
  guard    who may read what: tenant (owner) + department + flagged-document rules, applied by the SERVER on every query

Security rules (enforced here, not in the browser):
  * tenant isolation: every query is scoped to the caller's owner (VectorLab.for_owner)
  * document-level access: a document is readable if its department is one of the caller's departments, or it is marked visibility "all";
    an admin may read every department. A filter sent by the caller is ANDed with this rule, so it can narrow but never widen
  * documents that look like they contain orders for the model are flagged at upload and never used as context
  * a missing or foreign document is reported as "not found" (404), never "forbidden", so existence is not leaked
  * logs hold ids, timings and outcomes, not the question text, unless VECTOR_LAB_LOG_TEXT=1
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

from .engine import VectorLab, filter_sql
from .rag import REFUSAL, extract_answer, unsupported_numbers

ALLOWED_TYPES = ("txt", "md", "pdf", "docx")
MAX_BYTES = 2_000_000
MAX_CHARS = 100_000
MAX_HISTORY = 6
DEFAULT_GATE = 0.60


class AssistantError(Exception):
    """An error that is safe to show to the caller: a status, a stable code and a plain message."""

    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message


@dataclass(frozen=True)
class Caller:
    owner: str  # the tenant: one person's or one team's private space
    departments: tuple[str, ...] = ()  # departments this caller may read and upload to
    admin: bool = False  # may read every department and load the sample documents


# ---------------------------------------------------------------- who is calling
def load_users() -> dict[str, Caller] | None:
    """VECTOR_LAB_USERS = {"<api key>": {"owner": "alice", "departments": ["hr"], "admin": false}, ...}  (set in the environment, never in code).
    None when it is not set: the service then runs in single-user local mode."""
    raw = os.environ.get("VECTOR_LAB_USERS")
    if not raw:
        return None
    return {k: Caller(str(v["owner"]), tuple(v.get("departments", ())), bool(v.get("admin", False))) for k, v in json.loads(raw).items()}


# ---------------------------------------------------------------- files
_PATTERNS = {
    "ignore-instructions": r"ignore\s+(all\s+|any\s+)?(the\s+)?(previous|above|prior|earlier)\s+(instructions|rules|messages)",
    "disregard-instructions": r"disregard\s+.{0,40}(instructions|rules|guidelines)",
    "role-change": r"you\s+are\s+now\s+(a|an|the)\b",
    "system-prompt": r"(reveal|show|print|repeat)\s+.{0,30}(system\s+prompt|your\s+instructions)",
    "secret-request": r"(reveal|send|output|leak)\s+.{0,30}(password|secret|api\s*key|token|credentials)",
    "fake-instructions": r"(new|updated|override)\s+instructions?\s*:",
    "fake-system-tag": r"<\s*/?\s*(system|assistant)\s*>",
    "always-answer": r"(always|only)\s+(answer|reply|respond)\s+(with|that|yes|no)\b",
}
_COMPILED = {k: re.compile(v, re.I | re.S) for k, v in _PATTERNS.items()}


def screen_text(text: str) -> list[str]:
    """Names of the patterns that look like orders to a model hidden inside a document. A hit does not prove an attack; it quarantines the file."""
    return [name for name, rx in _COMPILED.items() if rx.search(text)]


def clean(text: str) -> str:
    text = text.replace("\x00", "")
    text = re.sub(r"[ \t\f\v]+", " ", text)
    text = re.sub(r"[ \t]*\n[ \t]*", "\n", text)
    return re.sub(r"\n{3,}", "\n\n", text).strip()


def extract_text(filename: str, data: bytes) -> str:
    """Plain text from a txt, md, pdf or docx file, cleaned. Raises AssistantError for a type, size or content that is not accepted."""
    ext = filename.lower().rsplit(".", 1)[-1] if "." in filename else ""
    if ext not in ALLOWED_TYPES:
        raise AssistantError(415, "unsupported_type", f"Only {', '.join('.' + e for e in ALLOWED_TYPES)} files are accepted.")
    if len(data) > MAX_BYTES:
        raise AssistantError(413, "too_large", f"The file is larger than {MAX_BYTES // 1_000_000} MB.")
    try:
        if ext in ("txt", "md"):
            text = data.decode("utf-8", errors="replace")
        elif ext == "pdf":
            from pypdf import PdfReader
            reader = PdfReader(io.BytesIO(data))
            if reader.is_encrypted:
                raise AssistantError(422, "encrypted", "The PDF is password protected.")
            text = "\n\n".join((p.extract_text() or "") for p in reader.pages[:300])
        else:
            import docx
            d = docx.Document(io.BytesIO(data))
            cells = [c.text for t in d.tables for row in t.rows for c in row.cells]
            text = "\n\n".join([p.text for p in d.paragraphs if p.text.strip()] + [c for c in cells if c.strip()])
    except AssistantError:
        raise
    except Exception:
        raise AssistantError(422, "unreadable", "The file could not be read. It may be damaged.")
    text = clean(text)[:MAX_CHARS]
    if len(text) < 20:
        raise AssistantError(422, "no_text", "No readable text was found in the file (a scanned PDF needs OCR, which is not built).")
    return text


def make_doc_id(filename: str, data: bytes) -> str:
    stem = re.sub(r"[^a-z0-9]+", "-", filename.rsplit(".", 1)[0].lower()).strip("-")[:40] or "document"
    return f"{stem}-{hashlib.sha1(data).hexdigest()[:6]}"


# ---------------------------------------------------------------- access rule
def visible_filter(caller: Caller) -> dict:
    """Which documents this caller may read (no flagged-document rule here: that one is for answering, not for listing)."""
    if caller.admin:
        return {}
    alts: list[dict] = [{"visibility": "all"}]
    if caller.departments:
        alts.insert(0, {"department": {"$in": list(caller.departments)}})
    return {"$or": alts}


def answer_filter(caller: Caller, user_filter: dict | None) -> dict:
    """The filter used for retrieval: access rule AND not flagged AND whatever the caller asked for. A caller's filter can only narrow the result."""
    parts = [{"flagged": {"$ne": True}}]
    vis = visible_filter(caller)
    if vis:
        parts.append(vis)
    if user_filter:
        parts.append(user_filter)
    return {"$and": parts}


# ---------------------------------------------------------------- follow-up questions
_CONTINUES = re.compile(r"^\s*(and|also|then|so|but|what about|how about|same for|why)\b", re.I)
_PRONOUN = re.compile(r"\b(it|that|they|them|those|this|its|their)\b", re.I)


def rewrite_followup(question: str, history: list[dict] | None) -> str:
    """A short question that leans on the conversation is joined with the last question of the person, so retrieval gets a complete query.
    It leans on the conversation only if it starts like a continuation ("and in Germany?", "what about the US?") or points back with a
    pronoun ("how many days is it?"). A complete question ("What is the company dress code?") is never rewritten, whatever came before.
    Rule-based on purpose: the completed question is shown to the user, who can see exactly what was searched."""
    if not history:
        return question
    last_user = next((h["content"] for h in reversed(history) if h["role"] == "user"), None)
    if last_user and len(question.split()) <= 7 and (_CONTINUES.search(question) or _PRONOUN.search(question)):
        return f"{last_user.rstrip(' ?.')} {question}"[:500]
    return question


def clean_history(history: list[dict] | None) -> list[dict]:
    out = []
    for h in (history or [])[-MAX_HISTORY:]:
        role, content = h.get("role"), h.get("content")
        if role in ("user", "assistant") and isinstance(content, str) and content.strip():
            out.append({"role": role, "content": content.strip()[:500]})
    return out


# ---------------------------------------------------------------- logging
class RequestLog:
    """One JSON line per request: ids, timings and outcomes. The question text is NOT stored unless VECTOR_LAB_LOG_TEXT=1."""

    def __init__(self, path: Path | None):
        self.path, self._lock = path, threading.Lock()
        if path:
            path.parent.mkdir(parents=True, exist_ok=True)

    def record(self, **fields) -> None:
        line = json.dumps({"ts": round(time.time(), 3), **fields}, default=str)
        if self.path:
            with self._lock, self.path.open("a", encoding="utf-8") as f:
                f.write(line + "\n")

    def recent(self, owner: str, n: int = 20) -> list[dict]:
        if not self.path or not self.path.exists():
            return []
        rows = [json.loads(x) for x in self.path.read_text(encoding="utf-8").splitlines() if x.strip()]
        return [r for r in rows if r.get("owner") == owner][-n:]


Ask = Callable[[str], tuple[str, dict]]


@dataclass
class Assistant:
    lab: VectorLab
    log: RequestLog = field(default_factory=lambda: RequestLog(None))
    llm: Ask | None = None  # a model call (prompt -> text, usage); None = answers are extractive

    # ------------------------------------------------------------ upload
    def upload(self, caller: Caller, filename: str, data: bytes, department: str | None = None, region: str = "All", year: int = 2025,
               visibility: str = "department", request_id: str = "") -> dict:
        t0 = time.perf_counter()
        base = Path(filename.replace("\\", "/")).name[:120]
        try:
            department = (department or (caller.departments[0] if caller.departments else "general")).lower()
            if not re.fullmatch(r"[a-z0-9_-]{2,30}", department):
                raise AssistantError(400, "bad_department", "The department name is not valid.")
            if not caller.admin and department not in caller.departments:
                raise AssistantError(403, "department_not_allowed", "You can only add documents to your own departments.")
            if visibility not in ("department", "all"):
                raise AssistantError(400, "bad_visibility", 'visibility must be "department" or "all".')
            if not 1990 <= int(year) <= 2100:
                raise AssistantError(400, "bad_year", "The year is not valid.")
            text = extract_text(base, data)
            hits = screen_text(text)
            doc_id = make_doc_id(base, data)
            lab = self.lab.for_owner(caller.owner)
            n = lab.ingest(doc_id, base, text, department=department, region=region[:30], year=int(year), visibility=visibility,
                           flagged=bool(hits), source_file=base)
        except AssistantError as e:
            self.log.record(request_id=request_id, owner=caller.owner, endpoint="upload", outcome="error", code=e.code, ms=round((time.perf_counter() - t0) * 1000, 1))
            raise
        out = {"doc_id": doc_id, "title": base, "chunks": n, "department": department, "visibility": visibility, "flagged": bool(hits),
               "warning": ("This file contains text that looks like instructions to an AI model (" + ", ".join(hits) + "). It was stored but will not be used to answer questions.") if hits else None}
        self.log.record(request_id=request_id, owner=caller.owner, endpoint="upload", outcome="flagged" if hits else "ok", chunks=n, department=department,
                        ms=round((time.perf_counter() - t0) * 1000, 1))
        return out

    # ------------------------------------------------------------ list and delete
    def documents(self, caller: Caller) -> list[dict]:
        params: list = []
        cond = filter_sql(visible_filter(caller) or None, params)
        lab = self.lab.for_owner(caller.owner)
        with lab.pool.connection() as c:
            rows = c.execute(f"select doc_id, title, metadata, count(*) from {lab.table} where owner = %s and {cond} group by doc_id, title, metadata order by doc_id",
                             [caller.owner, *params]).fetchall()
        return [{"doc_id": d, "title": t, "chunks": n, **{k: v for k, v in m.items() if k in ("department", "region", "year", "visibility", "flagged", "source_file")}} for d, t, m, n in rows]

    def delete(self, caller: Caller, doc_id: str, request_id: str = "") -> int:
        if not any(d["doc_id"] == doc_id for d in self.documents(caller)):
            self.log.record(request_id=request_id, owner=caller.owner, endpoint="delete", outcome="not_found")
            raise AssistantError(404, "not_found", "No such document.")
        n = self.lab.for_owner(caller.owner).delete(doc_id)
        self.log.record(request_id=request_id, owner=caller.owner, endpoint="delete", outcome="ok", chunks=n)
        return n

    # ------------------------------------------------------------ ask
    # DEBUGGING: the steps below carry the same numbers as vector-lab/DEBUGGING.md. Each can be called alone in a Python shell.
    def ask(self, caller: Caller, question: str, history: list[dict] | None = None, k: int = 3, mode: str = "hybrid", min_score: float = DEFAULT_GATE,
            user_filter: dict | None = None, request_id: str = "") -> dict:
        started = time.perf_counter()
        question = (question or "").strip()
        try:
            self._check_ask_settings(question, k, min_score)                                        # step 2
            standalone = rewrite_followup(question, clean_history(history))                         # step 3
            hits, best_similarity = self._retrieve(caller, standalone, mode, k, user_filter)        # steps 4 and 5
            retrieve_ms = (time.perf_counter() - started) * 1000
            reply = self._write_answer(standalone, hits, best_similarity, min_score)                # steps 6, 7 and 8
        except AssistantError as e:
            self.log.record(request_id=request_id, owner=caller.owner, endpoint="ask", outcome="error", code=e.code, ms=_ms_since(started))
            raise
        total_ms = (time.perf_counter() - started) * 1000
        generator = "llm" if self.llm else "extractive"
        best = None if best_similarity is None else round(best_similarity, 3)
        self._log_ask(request_id, caller, question, standalone, mode, k, hits, best, reply, retrieve_ms, total_ms, generator)  # step 9
        return {"request_id": request_id, "answer": reply["text"], "refused": reply["refused"], "reason": reply["reason"], "cited": reply["cited"],
                "standalone_question": standalone, "rewritten": standalone != question, "sources": _sources_for_reply(hits), "best_similarity": best,
                "gate": min_score, "generator": generator,
                "timing_ms": {"retrieve": round(retrieve_ms, 1), "generate": round(reply["generate_ms"], 1), "total": round(total_ms, 1)}}

    def _check_ask_settings(self, question: str, k: int, min_score: float) -> None:
        if not 2 <= len(question) <= 500:
            raise AssistantError(400, "bad_question", "The question must be between 2 and 500 characters.")
        if not 1 <= k <= 10:
            raise AssistantError(400, "bad_k", "k must be between 1 and 10.")
        if not 0.0 <= min_score <= 1.0:
            raise AssistantError(400, "bad_min_score", "min_score must be between 0 and 1.")

    def _retrieve(self, caller: Caller, question: str, mode: str, k: int, user_filter: dict | None):
        """The chunks this caller may read, and the best semantic similarity among them. The access filter is built here, on the server."""
        lab = self.lab.for_owner(caller.owner)
        try:
            access_filter = answer_filter(caller, user_filter)
            hits, _ = lab.search(question, mode, k, access_filter)
            best_semantic, _ = lab.search(question, "semantic", 1, access_filter)
        except ValueError as e:  # a bad mode or a bad filter from the caller
            raise AssistantError(400, "bad_request", str(e))
        return hits, (best_semantic[0].sem if best_semantic else None)

    def _write_answer(self, question: str, hits: list, best_similarity: float | None, min_score: float) -> dict:
        """Gate, then write the answer (extractive or language model), then verify it. Returns the answer and how it was made."""
        reply = {"text": REFUSAL, "cited": None, "refused": True, "reason": None, "generate_ms": 0.0, "usage": {}, "verified": True}
        if not hits or best_similarity is None or best_similarity < min_score:                      # step 6: the evidence gate
            reply["reason"] = "not enough evidence in the documents you may read"
            return reply
        started = time.perf_counter()
        if self.llm is None:                                                                         # step 7: extractive answer
            reply["text"], reply["cited"] = extract_answer(question, hits)
            reply["refused"] = reply["cited"] is None
        else:                                                                                        # step 7: language-model answer
            from .rag_llm import build_prompt, parse_answer
            try:
                raw, reply["usage"] = self.llm(build_prompt(question, hits))
            except Exception:
                raise AssistantError(503, "model_unavailable", "The language model could not be reached. Try again, or turn the model off to get extractive answers.")
            reply["text"], reply["cited"], reply["refused"] = parse_answer(raw, len(hits))
            unverified = not reply["refused"] and (reply["cited"] is None or unsupported_numbers(reply["text"], hits, reply["cited"]))  # step 8
            if unverified:
                reply.update(text=REFUSAL, cited=None, refused=True, verified=False, reason="the model's answer could not be verified against the documents")
        reply["generate_ms"] = (time.perf_counter() - started) * 1000
        return reply

    def _log_ask(self, request_id, caller, question, standalone, mode, k, hits, best, reply, retrieve_ms, total_ms, generator) -> None:
        question_field = {"question": question} if os.environ.get("VECTOR_LAB_LOG_TEXT") == "1" else {"question_chars": len(question)}
        self.log.record(request_id=request_id, owner=caller.owner, endpoint="ask", outcome="refused" if reply["refused"] else "answered", mode=mode, k=k,
                        hits=len(hits), best_similarity=best, followup=standalone != question, verified=reply["verified"], retrieve_ms=round(retrieve_ms, 1),
                        generate_ms=round(reply["generate_ms"], 1), total_ms=round(total_ms, 1), prompt_tokens=reply["usage"].get("prompt_tokens", 0),
                        output_tokens=reply["usage"].get("output_tokens", 0), generator=generator, **question_field)


def _ms_since(started: float) -> float:
    return round((time.perf_counter() - started) * 1000, 1)


def _sources_for_reply(hits: list) -> list[dict]:
    """The numbered chunks shown to the user; the answer's [n] points at these."""
    return [{"n": n, "doc_id": h.doc_id, "title": h.title, "text": h.text, "score": h.score, "similarity": h.sem,
             "department": h.metadata.get("department"), "region": h.metadata.get("region"), "year": h.metadata.get("year")} for n, h in enumerate(hits, start=1)]


def new_request_id() -> str:
    return uuid.uuid4().hex[:12]

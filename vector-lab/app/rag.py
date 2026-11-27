"""Week 2, Day 4: a small RAG pipeline on the Day 3 vector store, and the checks that score it.

Stages (kept separate, so a failure can be placed in one of them):
  1. retrieve   VectorLab.search (semantic or hybrid), optional metadata filter of the signed-in user
  2. gate       optional evidence gate: refuse when the best similarity is below a threshold
  3. generate   EXTRACTIVE answer: the sentence of the retrieved text that shares most words with the question, cited as [n].
                No language model is used here, so this measures retrieval and the answer rule, not a model's writing.
                (A language-model generator would plug in at the same place; it was not run on Day 4.)

Scoring is plain code with no model: retrieval (hit, MRR, context precision, context recall), answer (correct, grounded, citation,
refusal) and a failure label per question.
"""
from __future__ import annotations

import re
import time
from dataclasses import dataclass

from .engine import Hit, VectorLab
from .rag_eval_set import RQ

REFUSAL = "I could not find this in the approved documents."
_WORD = re.compile(r"[a-z0-9]+")
_STOP = {"the", "a", "an", "of", "to", "is", "are", "do", "does", "how", "what", "when", "who", "which", "i", "my", "can", "or", "and", "in", "for",
         "after", "many", "much", "be", "it", "on", "at", "by", "me", "you", "we", "with", "from", "as", "that", "this", "get", "long", "often"}
_SENT = re.compile(r"(?<=[.!?])\s+|\n+")


def norm(text: str) -> str:
    return " ".join(_WORD.findall(text.lower()))


def sentences(text: str) -> list[str]:
    return [s.strip() for s in _SENT.split(text) if s.strip()]


def _content_words(text: str) -> set[str]:
    return {w for w in _WORD.findall(text.lower()) if w not in _STOP}


def extract_answer(question: str, hits: list[Hit]) -> tuple[str, int | None]:
    """The best sentence of the retrieved text for this question, and the number [n] of the chunk it came from."""
    qw = _content_words(question)
    best, best_score, best_n = "", 0, None
    for n, h in enumerate(hits, start=1):
        for s in sentences(h.text):
            score = len(qw & _content_words(s))
            if score > best_score:
                best, best_score, best_n = s, score, n
    if best_n is None:
        return REFUSAL, None
    return f"{best} [{best_n}]", best_n


@dataclass
class Config:
    name: str
    mode: str = "semantic"
    k: int = 3
    use_filter: bool = False
    gate: float | None = None  # refuse when the best semantic similarity is below this
    rerank_from: int | None = None  # retrieve this many chunks, then let a cross-encoder pick the best k


@dataclass
class Result:
    q: RQ
    answer: str
    cited: int | None
    hits: list[Hit]
    refused: bool
    best_sim: float | None
    retrieve_ms: float = 0.0
    gen_ms: float = 0.0
    prompt_tokens: int = 0
    output_tokens: int = 0


_reranker = None


def rerank(question: str, hits: list[Hit], k: int) -> list[Hit]:
    """A cross-encoder reads the question and each chunk TOGETHER and scores how well the chunk answers it (slower, more precise than vectors)."""
    global _reranker
    if _reranker is None:
        from fastembed.rerank.cross_encoder import TextCrossEncoder
        _reranker = TextCrossEncoder(model_name="Xenova/ms-marco-MiniLM-L-6-v2")
    scores = list(_reranker.rerank(question, [h.text for h in hits]))
    order = sorted(range(len(hits)), key=lambda i: -scores[i])[:k]
    return [Hit(hits[i].chunk_id, hits[i].doc_id, hits[i].title, hits[i].text, hits[i].metadata, round(float(scores[i]), 4), r + 1, hits[i].sem) for r, i in enumerate(order)]


def retrieve(lab: VectorLab, q: RQ, cfg: Config) -> tuple[list[Hit], float | None, float]:
    """Stage 1 (and the gate's input): the chunks, the best semantic similarity, and the time taken in ms."""
    t0 = time.perf_counter()
    flt = q.filter if cfg.use_filter and q.filter else None
    hits, _ = lab.search(q.question, cfg.mode, cfg.rerank_from or cfg.k, flt)
    if cfg.rerank_from:
        hits = rerank(q.question, hits, cfg.k)
    sem, _ = lab.search(q.question, "semantic", 1, flt)
    return hits, (sem[0].sem if sem else None), (time.perf_counter() - t0) * 1000


def answer(lab: VectorLab, q: RQ, cfg: Config) -> Result:
    hits, best_sim, ms = retrieve(lab, q, cfg)
    if cfg.gate is not None and (best_sim is None or best_sim < cfg.gate):
        return Result(q, REFUSAL, None, hits, True, best_sim, retrieve_ms=ms)
    t0 = time.perf_counter()
    text, n = extract_answer(q.question, hits)
    return Result(q, text, n, hits, n is None, best_sim, retrieve_ms=ms, gen_ms=(time.perf_counter() - t0) * 1000)


# ---- scoring (pure functions, no model, no database) ----
def retrieval_scores(q: RQ, hits: list[Hit]) -> dict:
    ids = [h.doc_id for h in hits]
    rank = next((i + 1 for i, d in enumerate(ids) if d in q.docs), None)
    return {"hit": rank is not None, "rr": 1 / rank if rank else 0.0,
            "precision": (sum(d in q.docs for d in ids) / len(ids)) if ids else 0.0,
            "context_recall": any(norm(q.phrase) in norm(h.text) for h in hits) if q.phrase else None}


def is_correct(q: RQ, answer_text: str) -> bool:
    return bool(q.phrase) and norm(q.phrase) in norm(answer_text)


def is_grounded(answer_text: str, hits: list[Hit], cited: int | None) -> bool:
    """Every sentence of the answer (without its [n] mark) appears word for word in the chunk it cites."""
    if cited is None or not 1 <= cited <= len(hits):
        return False
    body = re.sub(r"\s*\[\d+\]\s*$", "", answer_text)
    chunk = norm(hits[cited - 1].text)
    return all(norm(s) in chunk for s in sentences(body))


def citation_ok(q: RQ, hits: list[Hit], cited: int | None) -> bool:
    """The cited chunk belongs to a correct document and holds the expected fact."""
    if cited is None or not 1 <= cited <= len(hits) or not q.phrase:
        return False
    h = hits[cited - 1]
    return h.doc_id in q.docs and norm(q.phrase) in norm(h.text)


def failure_label(q: RQ, r: Result) -> str | None:
    """Where a failed question went wrong. None = passed."""
    if not q.docs:  # not in the documents: the right behaviour is a refusal
        return None if r.refused else "hallucination (answered a question the documents do not cover)"
    if r.refused:
        return "false refusal (the gate or the retrieval returned nothing)"
    rs = retrieval_scores(q, r.hits)
    if not rs["context_recall"]:
        return "retrieval failure (the fact is not in the retrieved text)"
    if not is_correct(q, r.answer):
        return "generation failure (the fact was retrieved but not in the answer)"
    if not citation_ok(q, r.hits, r.cited):
        return "citation failure (right words, wrong source)"
    return None


def summarise(results: list[Result]) -> dict:
    ans = [r for r in results if r.q.docs]
    oos = [r for r in results if not r.q.docs]
    n = len(ans)
    rs = [retrieval_scores(r.q, r.hits) for r in ans]
    return {
        "answerable": n, "not_in_documents": len(oos),
        "hit": round(100 * sum(x["hit"] for x in rs) / n, 1),
        "mrr": round(sum(x["rr"] for x in rs) / n, 3),
        "context_precision": round(100 * sum(x["precision"] for x in rs) / n, 1),
        "context_recall": round(100 * sum(bool(x["context_recall"]) for x in rs) / n, 1),
        "answer_correct": round(100 * sum(is_correct(r.q, r.answer) for r in ans) / n, 1),
        "grounded": round(100 * sum(is_grounded(r.answer, r.hits, r.cited) for r in ans if not r.refused) / max(sum(not r.refused for r in ans), 1), 1),
        "citation_ok": round(100 * sum(citation_ok(r.q, r.hits, r.cited) for r in ans) / n, 1),
        "false_refusals": sum(r.refused for r in ans),
        "refused_correctly": sum(r.refused for r in oos),
        "hallucinated": sum(not r.refused for r in oos),
    }


# ---- extra checks: unsupported numbers, relevance, regression ----
_NUM = re.compile(r"\d[\d,]*(?:\.\d+)?")


def unsupported_numbers(answer_text: str, hits: list[Hit], cited: int | None) -> list[str]:
    """Numbers that appear in the answer but not in the chunk it cites: the cheapest hallucination check for facts like days, amounts and limits."""
    if cited is None or not 1 <= cited <= len(hits):
        return _NUM.findall(re.sub(r"\[\d+\]", "", answer_text))
    context = hits[cited - 1].text
    body = re.sub(r"\[\d+\]", "", answer_text)
    return [n for n in _NUM.findall(body) if n.strip(",") not in context.replace(",", "") and n not in context]


def answer_relevance(lab: VectorLab, question: str, answer_text: str) -> float:
    """How much the answer is about the question (embedding similarity, 0 to 1). A fluent answer to a different question scores low."""
    return lab.similarity(question, re.sub(r"\s*\[\d+\]\s*$", "", answer_text))


def context_relevance(lab: VectorLab, question: str, hits: list[Hit]) -> float:
    """Average similarity between the question and the retrieved chunks: how much of the context is on topic."""
    return round(sum(lab.similarity(question, h.text) for h in hits) / len(hits), 4) if hits else 0.0


def compare_to_baseline(baseline: dict, current: dict, tolerance: float = 2.0) -> list[str]:
    """Regression check: which headline numbers got worse than the saved baseline by more than `tolerance` points."""
    worse = []
    for name, base in baseline.items():
        cur = current.get(name)
        if cur is None:
            worse.append(f"{name}: configuration missing")
            continue
        for key in ("hit", "context_recall", "answer_correct", "citation_ok"):
            if cur[key] < base[key] - tolerance:
                worse.append(f"{name}: {key} fell from {base[key]} to {cur[key]}")
        if cur["mrr"] < base["mrr"] - tolerance / 100:
            worse.append(f"{name}: mrr fell from {base['mrr']} to {cur['mrr']}")
    return worse

"""The Day 3 query pipeline as a LangGraph graph:

    plan -> (route by mode) -> semantic | keyword | hybrid -> gate -> END

plan  : reads the question for a region or year ("in Germany", "in 2025") and adds it to the metadata filter (a rule-based self-query).
route : a conditional edge picks the search node from the requested mode.
gate  : the evidence check. If the best passage's cosine similarity is below `min_score`, the answer is "not enough evidence".
"""
from __future__ import annotations

import re
from typing import Any, TypedDict

from langgraph.graph import END, StateGraph

from .engine import Hit, Timing, VectorLab

REGIONS = {"uk": "UK", "united kingdom": "UK", "germany": "Germany", "german": "Germany", "us": "US", "usa": "US", "united states": "US"}


class State(TypedDict, total=False):
    question: str
    mode: str
    k: int
    filter: dict | None
    auto_filter: bool
    applied_filter: dict | None
    min_score: float
    hits: list[Hit]
    timing: Timing
    enough_evidence: bool
    trace: list[str]


def build(lab: VectorLab):
    def plan(s: State) -> State:
        flt, notes = s.get("filter"), []
        if s.get("auto_filter") and not flt:
            q = s["question"].lower()
            found = [v for key, v in REGIONS.items() if re.search(rf"\b{re.escape(key)}\b", q)]
            year = re.search(r"\b(20[12]\d)\b", q)
            conds: list[dict[str, Any]] = []
            if found:
                conds.append({"region": found[0]})
            if year:
                conds.append({"year": int(year.group(1))})
            if conds:
                flt = conds[0] if len(conds) == 1 else {"$and": conds}
                notes.append(f"plan: filter from the question {flt}")
        return {"applied_filter": flt, "trace": [*s.get("trace", []), *notes, f"plan: mode={s['mode']} k={s['k']} filter={flt}"]}

    def make_search(mode: str):
        def run(s: State) -> State:
            hits, t = lab.search(s["question"], mode, s["k"], s.get("applied_filter"))
            return {"hits": hits, "timing": t, "trace": [*s["trace"], f"{mode}: {len(hits)} passages in {t.search_ms} ms"]}
        return run

    def gate(s: State) -> State:
        hits = s.get("hits", [])
        best = max((h.sem for h in hits if h.sem is not None), default=None)
        if best is None and hits:  # keyword-only results carry no similarity: compare through a vector lookup of the top passage
            best = lab.similarity(s["question"], hits[0].text)
        ok = bool(hits) and best is not None and best >= s["min_score"]
        return {"enough_evidence": ok, "trace": [*s["trace"], f"gate: best similarity {best if best is None else round(best, 3)} vs {s['min_score']} -> {'enough' if ok else 'not enough evidence'}"]}

    g = StateGraph(State)
    g.add_node("plan", plan)
    for m in ("semantic", "keyword", "hybrid"):
        g.add_node(m, make_search(m))
    g.add_node("gate", gate)
    g.set_entry_point("plan")
    g.add_conditional_edges("plan", lambda s: s["mode"], {m: m for m in ("semantic", "keyword", "hybrid")})
    for m in ("semantic", "keyword", "hybrid"):
        g.add_edge(m, "gate")
    g.add_edge("gate", END)
    return g.compile()


def run(graph, question: str, mode: str = "hybrid", k: int = 3, flt: dict | None = None, min_score: float = 0.0, auto_filter: bool = False) -> State:
    return graph.invoke({"question": question, "mode": mode, "k": k, "filter": flt, "min_score": min_score, "auto_filter": auto_filter, "trace": []})

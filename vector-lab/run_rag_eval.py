"""Week 2, Day 4 evaluation. Run:  python run_rag_eval.py [--llm] [--check] [--save-baseline]
Results go to results/rag_eval.json and rag_eval.csv.

Runs the same 20 questions through configurations of the RAG pipeline, each adding ONE change, and reports retrieval and answer quality
separately, where every failed question went wrong, relevance, latency and tokens:

  C0 baseline        semantic search, top 3, no filter, no gate
  C1 + filter        C0 plus the user's profile filter (region, year)
  C2 + hybrid        C1 with hybrid search (semantic + keyword, reciprocal rank fusion)
  C3 + gate          C2 plus an evidence gate: refuse when the best similarity is below 0.60 (fixed before the run, not tuned on it)
  C4 + rerank        C1 with a cross-encoder reranker: retrieve 10, keep the best 3

  --llm              also run C1 and C4 with a language model writing the answer (local Ollama, qwen2.5:3b), plus an LLM judge for groundedness
                     compared with the plain-code check. Needs Ollama running with enough free memory.
  --save-baseline    store the current numbers as results/rag_eval_baseline.json
  --check            regression check: exit with an error if a headline number is worse than the saved baseline
"""
from __future__ import annotations

import csv
import json
import statistics as st
import sys
from pathlib import Path

from app.corpus import DOCS
from app.db import database_url, describe
from app.engine import MODEL, VectorLab
from app.rag import Config, answer, answer_relevance, compare_to_baseline, context_relevance, failure_label, summarise, unsupported_numbers
from app.rag_eval_set import RAG_QUESTIONS

OUT = Path(__file__).parent / "results"
OUT.mkdir(exist_ok=True)
BASELINE = OUT / "rag_eval_baseline.json"
CONFIGS = [Config("C0 baseline", "semantic", 3, False, None), Config("C1 + filter", "semantic", 3, True, None),
           Config("C2 + hybrid", "hybrid", 3, True, None), Config("C3 + gate 0.60", "hybrid", 3, True, 0.60),
           Config("C4 + rerank 10 to 3", "semantic", 3, True, None, rerank_from=10)]
LLM_CONFIGS = [Config("L1 filter + LLM answer", "semantic", 3, True, None), Config("L4 rerank + LLM answer", "semantic", 3, True, None, rerank_from=10)]
HEADLINE = ("hit", "mrr", "context_precision", "context_recall", "answer_correct", "citation_ok")


def extras(lab: VectorLab, res: list) -> dict:
    """Answer relevance and context relevance (embedding similarity), retrieval latency and tokens."""
    ans = [r for r in res if not r.refused]
    rel = [answer_relevance(lab, r.q.question, r.answer) for r in ans] or [0.0]
    ctx = [context_relevance(lab, r.q.question, r.hits) for r in res]
    return {"answer_relevance": round(st.mean(rel), 3), "context_relevance": round(st.mean(ctx), 3),
            "retrieve_ms_p50": round(st.median(r.retrieve_ms for r in res), 1), "generate_ms_p50": round(st.median(r.gen_ms for r in res), 1),
            "prompt_tokens": sum(r.prompt_tokens for r in res), "output_tokens": sum(r.output_tokens for r in res)}


def report(lab: VectorLab, cfg: Config, res: list) -> dict:
    s = {**summarise(res), **extras(lab, res)}
    fails = [{"q": r.q.question, "kind": r.q.kind, "failure": failure_label(r.q, r), "answer": r.answer, "top": [h.doc_id for h in r.hits]} for r in res if failure_label(r.q, r)]
    print(f"\n== {cfg.name}: {s}")
    for f in fails:
        print("   FAIL", f["failure"], "|", f["q"], "| top:", f["top"], "| answer:", f["answer"][:80])
    return {"name": cfg.name, "mode": cfg.mode, "k": cfg.k, "filter": cfg.use_filter, "gate": cfg.gate, "rerank_from": cfg.rerank_from, **s, "failures": fails}


def llm_run(lab: VectorLab, out: dict) -> None:
    from app.rag_llm import answer_llm, judge_supported, ollama_ask
    ask = ollama_ask("qwen2.5:3b")
    for cfg in LLM_CONFIGS:
        res = [answer_llm(lab, q, cfg, ask) for q in RAG_QUESTIONS]
        rep = report(lab, cfg, res)
        agree, total, no_verdict = 0, 0, 0
        for r in res:
            if r.refused or r.cited is None:
                continue
            code_ok = not unsupported_numbers(r.answer, r.hits, r.cited)
            verdict = judge_supported(ask, r.hits[r.cited - 1].text, r.answer)
            if verdict is None:
                no_verdict += 1
                continue
            total += 1
            agree += verdict == code_ok
        rep["judge"] = {"model": "qwen2.5:3b", "compared": total, "agrees_with_number_check": agree, "no_verdict": no_verdict}
        print("   judge vs number check:", rep["judge"])
        out["configs"].append(rep)


if __name__ == "__main__":
    url = database_url()
    print("database:", describe(url), flush=True)
    lab = VectorLab(url, owner="rag_eval")
    for d in DOCS:
        lab.delete(d.doc_id)
    chunks = sum(lab.ingest(d.doc_id, d.title, d.body, department=d.department, region=d.region, year=d.year) for d in DOCS)
    result: dict = {"model": MODEL, "documents": len(DOCS), "chunks": chunks, "questions": len(RAG_QUESTIONS), "generator": "extractive (no language model) unless a row says LLM", "configs": []}
    for cfg in CONFIGS:
        result["configs"].append(report(lab, cfg, [answer(lab, q, cfg) for q in RAG_QUESTIONS]))
    if "--llm" in sys.argv:
        llm_run(lab, result)
    (OUT / "rag_eval.json").write_text(json.dumps(result, indent=1, default=str), encoding="utf-8")
    with open(OUT / "rag_eval.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["config", *HEADLINE, "answer_relevance", "context_relevance", "retrieve_ms_p50", "generate_ms_p50", "prompt_tokens", "output_tokens", "false_refusals", "refused_correctly", "hallucinated", "failed_questions"])
        for c in result["configs"]:
            w.writerow([c["name"], *[c[k] for k in HEADLINE], c["answer_relevance"], c["context_relevance"], c["retrieve_ms_p50"], c["generate_ms_p50"], c["prompt_tokens"], c["output_tokens"], c["false_refusals"], c["refused_correctly"], c["hallucinated"], len(c["failures"])])
    summary = {c["name"]: {k: c[k] for k in HEADLINE} for c in result["configs"] if not c["name"].startswith("L")}
    if "--save-baseline" in sys.argv:
        BASELINE.write_text(json.dumps(summary, indent=1), encoding="utf-8")
        print("baseline saved:", BASELINE)
    lab.close()
    if "--check" in sys.argv:
        worse = compare_to_baseline(json.loads(BASELINE.read_text(encoding="utf-8")), summary)
        print("\nREGRESSION CHECK:", "FAILED" if worse else "passed")
        for w_ in worse:
            print("  -", w_)
        sys.exit(1 if worse else 0)
    print("\nwritten:", OUT / "rag_eval.json")

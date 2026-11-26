"""Week 2, Day 3 evaluation. Run:  python run_eval.py [--scale]   (results go to results/)

Measures, over the fixed question set in app/corpus.py:
  hit@k        a chunk of a correct document is in the top K            (retrieval found the right place)
  MRR          1 / rank of the first correct chunk
  precision@k  share of the K chunks that belong to a correct document (how much noise the context carries)
  phrase@k     a top-K chunk contains the expected fact                 (context recall)
  filter       the same, with the metadata filter a careful user would apply
  latency      median and p95 of the search call, warm, per query
  gate         how well a similarity threshold separates answerable from not-in-the-documents questions
  --scale      HNSW index against exact (brute force) search as the number of chunks grows
"""
from __future__ import annotations

import csv
import json
import statistics as st
import sys
import tempfile
import time
from pathlib import Path

import numpy as np

from app.corpus import DOCS, QUESTIONS
from app.engine import MODEL, MODES, VectorLab

OUT = Path(__file__).parent / "results"
OUT.mkdir(exist_ok=True)


def build(path) -> tuple[VectorLab, dict]:
    lab = VectorLab(path)
    t0 = time.perf_counter()
    chunks = sum(lab.ingest(d.doc_id, d.title, d.body, department=d.department, region=d.region, year=d.year) for d in DOCS)
    return lab, {"documents": len(DOCS), "chunks": chunks, "ingest_seconds": round(time.perf_counter() - t0, 2)}


def score(lab: VectorLab, mode: str, k: int, use_filter: bool, kinds: set[str]) -> dict:
    rows, lat = [], []
    for q in QUESTIONS:
        if q.kind not in kinds:
            continue
        flt = q.filter if use_filter else None
        times = []
        for _ in range(5):
            hits, t = lab.search(q.question, mode, k, flt)
            times.append(t.search_ms)
        lat.append(st.median(times))
        ids = [h.doc_id for h in hits]
        rank = next((i + 1 for i, d in enumerate(ids) if d in q.docs), None)
        rows.append({"q": q.question, "kind": q.kind, "hit": rank is not None, "rr": 1 / rank if rank else 0.0,
                     "precision": sum(d in q.docs for d in ids) / max(len(ids), 1) if ids else 0.0,
                     "phrase": any(q.phrase.lower() in h.text.lower() for h in hits) if q.phrase else None,
                     "top": ids[0] if ids else None})
    n = len(rows)
    return {"mode": mode, "k": k, "filter": use_filter, "questions": n,
            "hit": round(100 * sum(r["hit"] for r in rows) / n, 1), "mrr": round(sum(r["rr"] for r in rows) / n, 3),
            "precision": round(100 * sum(r["precision"] for r in rows) / n, 1),
            "phrase": round(100 * sum(bool(r["phrase"]) for r in rows) / n, 1),
            "p50_ms": round(st.median(lat), 2), "p95_ms": round(float(np.percentile(lat, 95)), 2), "rows": rows}


def gate_analysis(lab: VectorLab) -> dict:
    ans, oos = [], []
    for q in QUESTIONS:
        hits, _ = lab.search(q.question, "semantic", 1, q.filter or None)
        (oos if q.kind == "out_of_scope" else ans).append((q.question, hits[0].sem if hits else 0.0))
    lo, hi = min(s for _, s in ans), max(s for _, s in oos)
    best, best_t = (-1, 0.0), 0.0
    for t in np.arange(0.3, 0.9, 0.005):
        ok = sum(s >= t for _, s in ans) + sum(s < t for _, s in oos)
        if ok > best[0] or (ok == best[0] and abs(t - (lo + hi) / 2) < abs(best_t - (lo + hi) / 2)):
            best, best_t = (ok, t), t
    t = round(float(best_t), 3)
    return {"answerable_min": round(lo, 3), "answerable_median": round(st.median(s for _, s in ans), 3),
            "not_in_documents_max": round(hi, 3), "separable": lo > hi, "threshold": t,
            "refused_correctly": sum(s < t for _, s in oos), "not_in_documents": len(oos),
            "wrongly_refused": sum(s < t for _, s in ans), "answerable": len(ans),
            "lowest_answerable": sorted(ans, key=lambda x: x[1])[:3], "highest_not_in_docs": sorted(oos, key=lambda x: -x[1])[:3]}


def scale() -> list[dict]:
    """Exact search and the HNSW index over the same vectors, as the number of chunks grows. The texts are generated, so this
    measures speed and approximation quality, not relevance."""
    import chromadb
    rng = np.random.default_rng(7)
    words = sorted({w for d in DOCS for w in d.body.lower().replace(".", " ").replace(",", " ").split() if w.isalpha() and len(w) > 3})
    lab = VectorLab(None)
    out = []
    for n in (1000, 5000, 10000):
        texts = [" ".join(rng.choice(words, size=24)) for _ in range(n)]
        t0 = time.perf_counter()
        vecs = np.array(lab.emb.embed_documents(texts), dtype=np.float32)
        embed_s = time.perf_counter() - t0
        client = chromadb.EphemeralClient()
        name = f"scale{n}"
        try:
            client.delete_collection(name)
        except Exception:
            pass
        col = client.create_collection(name, metadata={"hnsw:space": "cosine"})
        t0 = time.perf_counter()
        for i in range(0, n, 2000):
            col.add(ids=[str(j) for j in range(i, min(i + 2000, n))], embeddings=vecs[i:i + 2000].tolist(), documents=texts[i:i + 2000])
        build_s = time.perf_counter() - t0
        qs = vecs[rng.choice(n, size=50, replace=False)] + rng.normal(0, 0.02, (50, vecs.shape[1])).astype(np.float32)
        qs /= np.linalg.norm(qs, axis=1, keepdims=True)
        exact_t, ann_t, overlap = [], [], []
        for q in qs:
            t0 = time.perf_counter(); top = np.argsort(-(vecs @ q))[:10]; exact_t.append((time.perf_counter() - t0) * 1000)
            t0 = time.perf_counter(); r = col.query(query_embeddings=[q.tolist()], n_results=10); ann_t.append((time.perf_counter() - t0) * 1000)
            overlap.append(len(set(map(int, r["ids"][0])) & set(map(int, top))) / 10)
        out.append({"chunks": n, "embed_seconds": round(embed_s, 1), "index_build_seconds": round(build_s, 2),
                    "exact_p50_ms": round(st.median(exact_t), 2), "hnsw_p50_ms": round(st.median(ann_t), 2),
                    "hnsw_p95_ms": round(float(np.percentile(ann_t, 95)), 2), "recall_at_10_vs_exact": round(100 * st.mean(overlap), 1)})
        print("scale", out[-1], flush=True)
    return out


def md(rs: list[dict], cols=("mode", "k", "filter", "hit", "mrr", "precision", "phrase", "p50_ms", "p95_ms")) -> str:
    head = "| " + " | ".join(cols) + " |\n|" + "---|" * len(cols) + "\n"
    return head + "\n".join("| " + " | ".join(f"{r[c]}" for c in cols) + " |" for r in rs)


if __name__ == "__main__":
    tmp = tempfile.mkdtemp()
    lab, ingest = build(tmp)
    print("ingest", ingest, flush=True)
    result: dict = {"model": MODEL, "ingest": ingest, "questions": {k: sum(q.kind == k for q in QUESTIONS) for k in ("plain", "paraphrase", "filtered", "out_of_scope")}}
    answerable = {"plain", "paraphrase", "filtered"}
    result["matrix"] = [score(lab, m, k, False, answerable) for m in MODES for k in (1, 3, 5)]
    result["by_kind"] = [{**score(lab, m, 3, False, {kind}), "kind": kind} for m in MODES for kind in ("plain", "paraphrase", "filtered")]
    result["filter_effect"] = [score(lab, m, 3, f, {"filtered"}) for m in ("semantic", "hybrid") for f in (False, True)]
    result["gate"] = gate_analysis(lab)
    if "--scale" in sys.argv:
        result["scale"] = scale()
    for key in ("matrix", "by_kind", "filter_effect"):
        for r in result[key]:
            r.pop("rows", None) if key != "matrix" else None
    (OUT / "eval.json").write_text(json.dumps(result, indent=1, default=str), encoding="utf-8")
    with open(OUT / "eval.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f); w.writerow(["mode", "k", "filter", "hit_pct", "mrr", "precision_pct", "phrase_pct", "p50_ms", "p95_ms"])
        for r in result["matrix"]:
            w.writerow([r["mode"], r["k"], r["filter"], r["hit"], r["mrr"], r["precision"], r["phrase"], r["p50_ms"], r["p95_ms"]])
    print("\n## mode x K (answerable questions, no filter)\n" + md(result["matrix"]))
    print("\n## by question type, K=3\n" + md(result["by_kind"], ("kind", "mode", "hit", "mrr", "precision", "phrase")))
    print("\n## filtered questions, K=3, with and without the filter\n" + md(result["filter_effect"]))
    print("\n## gate\n" + json.dumps(result["gate"], indent=1, default=str))

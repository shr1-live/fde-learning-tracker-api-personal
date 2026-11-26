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
import time
from pathlib import Path

import numpy as np

from app.corpus import DOCS, QUESTIONS
from app.db import database_url, describe
from app.engine import MODEL, MODES, VectorLab

OUT = Path(__file__).parent / "results"
OUT.mkdir(exist_ok=True)


def build(url) -> tuple[VectorLab, dict]:
    lab = VectorLab(url, owner="eval")
    for d in DOCS:
        lab.delete(d.doc_id)
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
    """Exact search against the pgvector HNSW index over the same vectors as the number of chunks grows, and what a selective
    metadata filter does to an HNSW search. The texts are generated, so this measures speed and approximation, not relevance."""
    import psycopg
    from pgvector.psycopg import register_vector
    rng = np.random.default_rng(7)
    words = sorted({w for d in DOCS for w in d.body.lower().replace(".", " ").replace(",", " ").split() if w.isalpha() and len(w) > 3})
    lab = VectorLab(database_url(), owner="scale")
    N = 10000
    texts = [" ".join(rng.choice(words, size=24)) for _ in range(N)]
    t0 = time.perf_counter()
    all_vecs = np.array(lab.emb.embed_documents(texts), dtype=np.float32)
    embed_s = time.perf_counter() - t0
    print("embedded", N, round(embed_s, 1), "s", flush=True)
    out = []
    with psycopg.connect(database_url(), autocommit=True) as c:
        register_vector(c)
        for n in (1000, 5000, 10000):
            vecs = all_vecs[:n]
            c.execute("drop table if exists scale_t")
            c.execute("create table scale_t (id int primary key, grp10 int, grp100 int, embedding vector(384))")
            t0 = time.perf_counter()
            with c.cursor().copy("copy scale_t (id, grp10, grp100, embedding) from stdin (format binary)") as cp:
                cp.set_types(["int4", "int4", "int4", "vector"])
                for i in range(n):
                    cp.write_row((i, i % 10, i % 100, vecs[i]))
            copy_s = time.perf_counter() - t0
            t0 = time.perf_counter()
            c.execute("create index scale_hnsw on scale_t using hnsw (embedding vector_cosine_ops) with (m = 16, ef_construction = 64)")
            build_s = time.perf_counter() - t0
            c.execute("analyze scale_t")
            qs = vecs[rng.choice(n, size=50, replace=False)] + rng.normal(0, 0.02, (50, 384)).astype(np.float32)
            qs /= np.linalg.norm(qs, axis=1, keepdims=True)
            row = {"chunks": n, "embed_seconds_for_10000": round(embed_s, 1), "load_seconds": round(copy_s, 2), "index_build_seconds": round(build_s, 2)}
            for ef in (40, 100):
                c.execute(f"set hnsw.ef_search = {ef}")
                ann_t, ov = [], []
                for q in qs:
                    top = np.argsort(-(vecs @ q))[:10]
                    t0 = time.perf_counter(); r = c.execute("select id from scale_t order by embedding <=> %s limit 10", (q,)).fetchall(); ann_t.append((time.perf_counter() - t0) * 1000)
                    ov.append(len({x[0] for x in r} & set(map(int, top))) / 10)
                row[f"hnsw_ef{ef}_p50_ms"] = round(st.median(ann_t), 2)
                row[f"hnsw_ef{ef}_recall_at_10"] = round(100 * st.mean(ov), 1)
            exact_t = []
            c.execute("set enable_indexscan = off")
            for q in qs[:25]:
                t0 = time.perf_counter(); c.execute("select id from scale_t order by embedding <=> %s limit 10", (q,)).fetchall(); exact_t.append((time.perf_counter() - t0) * 1000)
            c.execute("set enable_indexscan = on")
            row["exact_sql_scan_p50_ms"] = round(st.median(exact_t), 2)
            if n == N:  # a filter that keeps 10% or 1% of the rows, with the index in use
                for col, share in (("grp10", "10%"), ("grp100", "1%")):
                    for ef in (40, 400):
                        c.execute(f"set hnsw.ef_search = {ef}")
                        c.execute("set enable_seqscan = off")
                        got = [len(c.execute(f"select id from scale_t where {col} = 3 order by embedding <=> %s limit 10", (q,)).fetchall()) for q in qs[:25]]
                        c.execute("set enable_seqscan = on")
                        row[f"filter_{share}_ef{ef}_rows_returned_of_10"] = round(st.mean(got), 1)
            out.append(row)
            print("scale", row, flush=True)
        ver = c.execute("select extversion from pg_extension where extname='vector'").fetchone()[0]
        c.execute("drop table if exists scale_t")
    for r in out:
        r["pgvector_version"] = ver
    lab.close()
    return out


def md(rs: list[dict], cols=("mode", "k", "filter", "hit", "mrr", "precision", "phrase", "p50_ms", "p95_ms")) -> str:
    head = "| " + " | ".join(cols) + " |\n|" + "---|" * len(cols) + "\n"
    return head + "\n".join("| " + " | ".join(f"{r[c]}" for c in cols) + " |" for r in rs)


if __name__ == "__main__":
    url = database_url()
    print("database:", describe(url), flush=True)
    lab, ingest = build(url)
    print("ingest", ingest, flush=True)
    result: dict = {"vector_db": "Postgres + pgvector", "pgvector_version": lab.stats()["pgvector_version"], "model": MODEL, "ingest": ingest, "questions": {k: sum(q.kind == k for q in QUESTIONS) for k in ("plain", "paraphrase", "filtered", "out_of_scope")}}
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
    (OUT / "eval_pgvector.json").write_text(json.dumps(result, indent=1, default=str), encoding="utf-8")
    with open(OUT / "eval_pgvector.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f); w.writerow(["mode", "k", "filter", "hit_pct", "mrr", "precision_pct", "phrase_pct", "p50_ms", "p95_ms"])
        for r in result["matrix"]:
            w.writerow([r["mode"], r["k"], r["filter"], r["hit"], r["mrr"], r["precision"], r["phrase"], r["p50_ms"], r["p95_ms"]])
    print("\n## mode x K (answerable questions, no filter)\n" + md(result["matrix"]))
    print("\n## by question type, K=3\n" + md(result["by_kind"], ("kind", "mode", "hit", "mrr", "precision", "phrase")))
    print("\n## filtered questions, K=3, with and without the filter\n" + md(result["filter_effect"]))
    print("\n## gate\n" + json.dumps(result["gate"], indent=1, default=str))

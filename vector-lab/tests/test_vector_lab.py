"""Day 3 tests on Postgres + pgvector. They use the real embedding model and a real Postgres with the vector extension:
VECTOR_LAB_DB_URL if set (a throw-away Supabase project), otherwise a local Postgres started by pgserver."""
import uuid

import pytest

from app.corpus import DOCS, QUESTIONS
from app.db import database_url
from app.engine import BgeEmbeddings, VectorLab, filter_sql, matches
from app.graph import build, run


@pytest.fixture(scope="session")
def url():
    return database_url(allow_local=True)


@pytest.fixture(scope="session")
def emb():
    return BgeEmbeddings()


def fresh(url, emb, **kw):
    """A lab with its own owner, so tests never see each other's rows."""
    return VectorLab(url, emb, owner=f"t-{uuid.uuid4().hex[:10]}", **kw)


@pytest.fixture(scope="module")
def lab(url, emb):
    lab = fresh(url, emb)
    for d in DOCS:
        lab.ingest(d.doc_id, d.title, d.body, department=d.department, region=d.region, year=d.year)
    yield lab
    for d in DOCS:
        lab.delete(d.doc_id)
    lab.close()


def test_V01_chunks_are_stored_with_metadata(lab):
    docs = {d["doc_id"]: d for d in lab.documents()}
    assert len(docs) == len(DOCS)
    assert docs["leave-de-2025"]["region"] == "Germany" and docs["leave-de-2025"]["year"] == 2025
    s = lab.stats()
    assert s["dimensions"] == 384 and s["vector_db"].startswith("Postgres")


def test_V02_semantic_search_finds_a_paraphrase(lab):
    hits, _ = lab.search("Can I work from home?", "semantic", 3)
    assert {h.doc_id for h in hits} & {"remote-all-2025", "remote-de-2025"}


@pytest.mark.parametrize("mode", ["semantic", "keyword", "hybrid"])
@pytest.mark.parametrize("flt", [{"region": "Germany"}, {"region": "UK"}, {"department": "finance"}, {"year": {"$gte": 2025}},
                                 {"$and": [{"region": "UK"}, {"year": {"$gte": 2025}}]}, {"$or": [{"region": "US"}, {"region": "Germany"}]},
                                 {"region": {"$in": ["US", "UK"]}}, {"region": {"$ne": "All"}}, {"year": {"$lt": 2025}}])
def test_V03_every_result_satisfies_the_filter(lab, mode, flt):
    hits, _ = lab.search("annual leave days hotel remote work", mode, 10, flt)
    assert hits, "the filter must not hide everything"
    assert all(matches(h.metadata, flt) for h in hits)


def test_V04_filter_picks_the_right_near_duplicate(lab):
    q = "How many days of paid annual leave do I get?"
    de, _ = lab.search(q, "hybrid", 1, {"region": "Germany"})
    uk, _ = lab.search(q, "hybrid", 1, {"$and": [{"region": "UK"}, {"year": {"$gte": 2025}}]})
    assert de[0].doc_id == "leave-de-2025" and "30 days" in de[0].text
    assert uk[0].doc_id == "leave-uk-2025" and "24 days" in uk[0].text


def test_V05_top_k_is_respected_and_ranks_are_ordered(lab):
    for mode in ("semantic", "keyword", "hybrid"):
        for k in (1, 3, 5):
            hits, _ = lab.search("security incident report", mode, k)
            assert len(hits) <= k and [h.rank for h in hits] == list(range(1, len(hits) + 1))
    sem, _ = lab.search("security incident report", "semantic", 5)
    assert [h.score for h in sem] == sorted((h.score for h in sem), reverse=True)


def test_V06_bad_settings_are_refused(lab):
    with pytest.raises(ValueError):
        lab.search("x", "fuzzy", 3)
    with pytest.raises(ValueError):
        lab.search("x", "semantic", 0)
    with pytest.raises(ValueError):
        lab.search("x", "keyword", 3, {"region": {"$like": "U%"}})


def test_V07_reingest_replaces_and_delete_removes(url, emb):
    lab = fresh(url, emb)
    text = "Alpha paragraph about apples and pears.\n\nSecond paragraph about oranges and plums."
    n1 = lab.ingest("a", "Doc A", text, region="All", year=2025)
    assert n1 >= 1 and lab.count() == n1
    lab.ingest("a", "Doc A", text, region="All", year=2025)
    assert lab.count() == n1, "re-ingest must not duplicate"
    lab.ingest("a", "Doc A", "A short replacement about bananas.", region="All", year=2025)
    assert not lab.search("oranges", "keyword", 3)[0], "the text index must forget removed text"
    assert lab.delete("a") == 1 and lab.count() == 0


def test_V08_stored_vectors_are_reused_after_a_new_connection(url, emb):
    a = fresh(url, emb)
    a.ingest("p", "Persisted", "The vault code rotates every 90 days and is held by the facilities team.", region="All", year=2025)
    b = VectorLab(url, emb, owner=a.owner)  # a new object, a new connection pool, the same database
    before = emb.calls
    hits, _ = b.search("how often does the vault code rotate", "semantic", 1)
    assert hits and hits[0].doc_id == "p"
    assert emb.calls == before, "searching must not re-embed stored passages"
    a.delete("p")


def test_V09_hybrid_keeps_a_keyword_hit_that_vectors_rank_low(lab):
    hits, _ = lab.search("3 written quotes", "hybrid", 3)
    assert "procurement-all-2025" in {h.doc_id for h in hits}


def test_V10_graph_routes_by_mode_and_gates_weak_evidence(lab):
    g = build(lab)
    ok = run(g, "How long must a password be?", "hybrid", 3, min_score=0.5)
    assert ok["enough_evidence"] and any("hybrid:" in t for t in ok["trace"])
    no = run(g, "What is the company dress code?", "semantic", 3, min_score=0.75)
    assert not no["enough_evidence"] and any("not enough evidence" in t for t in no["trace"])


def test_V11_graph_reads_a_region_from_the_question(lab):
    g = build(lab)
    r = run(g, "How many days of annual leave in Germany?", "hybrid", 3, auto_filter=True)
    assert r["applied_filter"] == {"region": "Germany"} and all(h.metadata["region"] == "Germany" for h in r["hits"])


def test_V12_documents_in_one_question_set_all_exist():
    ids = {d.doc_id for d in DOCS}
    assert all(set(q.docs) <= ids for q in QUESTIONS)
    assert {q.kind for q in QUESTIONS} == {"plain", "paraphrase", "filtered", "out_of_scope"}


# ---- what pgvector adds ----
def test_V13_one_owner_never_sees_or_deletes_anothers_documents(url, emb):
    a, b = fresh(url, emb), fresh(url, emb)
    a.ingest("secret", "Secret plan", "The merger with Contoso is planned for March and is confidential.", region="All", year=2025)
    for mode in ("semantic", "keyword", "hybrid"):
        assert not b.search("merger with Contoso", mode, 5)[0], f"{mode}: owner B must not see owner A's passages"
    assert b.count() == 0 and b.documents() == []
    assert b.delete("secret") == 0 and a.count() >= 1
    a.delete("secret")


def test_V14_vectors_from_another_model_are_not_compared(url, emb):
    lab = fresh(url, emb)
    lab.ingest("m", "Model test", "Standing desks are available on request from the facilities team.", region="All", year=2025)
    assert lab.search("standing desks", "semantic", 3)[0]
    lab.model = "some-other-model"  # as if the embedding model had been switched without re-embedding
    assert not lab.search("standing desks", "semantic", 3)[0], "old vectors must not be mixed with a different model's"
    lab.model = emb.model_name
    lab.delete("m")


@pytest.mark.parametrize("bad", [{"region'; drop table x; --": "UK"}, {"a b": 1}, {"$and": []}, {"region": {"$in": []}}, {"year": {"$gt": [1]}}])
def test_V15_a_hostile_filter_is_refused_not_executed(lab, bad):
    with pytest.raises(ValueError):
        lab.search("leave", "semantic", 3, bad)


def test_V16_a_hostile_question_is_only_data(lab):
    for q in ["'; drop table vector_chunks_384; --", "leave) | (hotel", "a:* & !b <-> c"]:
        for mode in ("semantic", "keyword", "hybrid"):
            lab.search(q, mode, 3)
    assert lab.count() > 0


def test_V17_filter_values_are_bound_not_pasted():
    params: list = []
    sql = filter_sql({"region": "UK'; select 1; --"}, params)
    assert "select" not in sql and len(params) == 1


def test_V18_the_hnsw_index_and_row_security_exist(lab):
    with lab.pool.connection() as c:
        idx = {r[0]: r[1] for r in c.execute("select indexname, indexdef from pg_indexes where tablename = %s", (lab.table,)).fetchall()}
        rls = c.execute("select relrowsecurity from pg_class where relname = %s", (lab.table,)).fetchone()[0]
    assert "hnsw" in idx[f"{lab.table}_hnsw"] and "vector_cosine_ops" in idx[f"{lab.table}_hnsw"]
    assert rls is True

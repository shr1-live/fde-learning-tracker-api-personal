"""Day 3 tests. They use the real embedding model and a real Chroma database in a temporary folder."""
import pytest

from app.corpus import DOCS, QUESTIONS
from app.engine import VectorLab, matches
from app.graph import build, run


@pytest.fixture(scope="module")
def lab(tmp_path_factory):
    lab = VectorLab(tmp_path_factory.mktemp("chroma"))
    for d in DOCS:
        lab.ingest(d.doc_id, d.title, d.body, department=d.department, region=d.region, year=d.year)
    return lab


def test_V01_chunks_are_stored_with_metadata(lab):
    docs = {d["doc_id"]: d for d in lab.documents()}
    assert len(docs) == len(DOCS)
    assert docs["leave-de-2025"]["region"] == "Germany" and docs["leave-de-2025"]["year"] == 2025
    assert lab.stats()["dimensions"] == 384


def test_V02_semantic_search_finds_a_paraphrase(lab):
    hits, _ = lab.search("Can I work from home?", "semantic", 3)
    assert {h.doc_id for h in hits} & {"remote-all-2025", "remote-de-2025"}


@pytest.mark.parametrize("mode", ["semantic", "keyword", "hybrid"])
@pytest.mark.parametrize("flt", [{"region": "Germany"}, {"region": "UK"}, {"department": "finance"}, {"year": {"$gte": 2025}},
                                 {"$and": [{"region": "UK"}, {"year": {"$gte": 2025}}]}, {"$or": [{"region": "US"}, {"region": "Germany"}]}])
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


def test_V07_reingest_replaces_and_delete_removes(tmp_path):
    lab = VectorLab(tmp_path)
    n1 = lab.ingest("a", "Doc A", "Alpha paragraph about apples and pears.\n\nSecond paragraph about oranges and plums.", region="All", year=2025)
    c1 = lab.count()
    lab.ingest("a", "Doc A", "Alpha paragraph about apples and pears.\n\nSecond paragraph about oranges and plums.", region="All", year=2025)
    assert lab.count() == c1 == n1, "re-ingest must not duplicate"
    lab.ingest("a", "Doc A", "A short replacement about bananas.", region="All", year=2025)
    hits, _ = lab.search("oranges", "keyword", 3)
    assert not hits, "keyword index must forget removed text"
    assert lab.delete("a") == 1 and lab.count() == 0


def test_V08_the_index_survives_a_restart(tmp_path):
    a = VectorLab(tmp_path)
    a.ingest("p", "Persisted", "The vault code rotates every 90 days and is held by the facilities team.", region="All", year=2025)
    del a
    b = VectorLab(tmp_path)
    hits, _ = b.search("how often does the vault code rotate", "semantic", 1)
    assert hits and hits[0].doc_id == "p"


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

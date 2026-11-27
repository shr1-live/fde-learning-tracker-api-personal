"""Week 2, Day 4: tests for the RAG evaluation set and the scoring code. Pure Python: no model, no database."""
import pytest

from app.corpus import DOCS
from app.engine import Hit, matches
from app.rag import (REFUSAL, Result, citation_ok, extract_answer, failure_label, is_correct, is_grounded, norm, retrieval_scores, summarise)
from app.rag_eval_set import RAG_QUESTIONS, RQ

BODY = {d.doc_id: d for d in DOCS}


def hit(doc_id, text, n=1):
    d = BODY.get(doc_id)
    return Hit(f"{doc_id}:{n}", doc_id, d.title if d else doc_id, text, {"region": d.region if d else "All", "year": d.year if d else 2025}, 0.5, n, 0.5)


# ---- the evaluation set itself must be valid, or the scores mean nothing ----
def test_set_size_and_mix():
    assert 15 <= len(RAG_QUESTIONS) <= 20
    assert sum(not q.docs for q in RAG_QUESTIONS) >= 3, "needs questions that are not in the documents"
    assert {q.kind for q in RAG_QUESTIONS} >= {"plain", "paraphrase", "filtered", "out_of_scope"}


def test_every_answerable_question_points_at_real_text():
    for q in RAG_QUESTIONS:
        if not q.docs:
            continue
        assert q.phrase, q.question
        assert all(d in BODY for d in q.docs), q.question
        assert any(norm(q.phrase) in norm(BODY[d].body) for d in q.docs), f"expected fact not in the source: {q.question}"


def test_filter_keeps_the_expected_source():
    for q in RAG_QUESTIONS:
        for d in q.docs:
            if q.filter:
                doc = BODY[d]
                assert matches({"region": doc.region, "year": doc.year, "department": doc.department}, q.filter), f"filter excludes its own source: {q.question}"


def test_out_of_scope_phrases_really_absent():
    corpus = norm(" ".join(d.body for d in DOCS))
    for term in ("dress code", "annual bonus", "stock options", "maternity"):
        assert term not in corpus


# ---- the scoring functions ----
Q = RQ("How long must a password be?", "At least 14 characters.", ("remote-all-2025",), "14 characters", "plain")
GOOD = hit("remote-all-2025", "Passwords must have at least 14 characters and multi-factor authentication is mandatory.")
NOISE = hit("expense-all-2025", "Business expenses are reimbursed within 14 days of a complete claim.", 2)


def test_extract_answer_picks_the_matching_sentence_and_cites_it():
    text, n = extract_answer(Q.question, [NOISE, GOOD])
    assert n == 2 and "14 characters" in text and text.endswith("[2]")


def test_extract_answer_refuses_when_nothing_overlaps():
    text, n = extract_answer("What is the company dress code?", [hit("remote-all-2025", "Zzz qqq.")])
    assert text == REFUSAL and n is None


def test_retrieval_scores():
    s = retrieval_scores(Q, [NOISE, GOOD])
    assert s["hit"] and s["rr"] == 0.5 and s["precision"] == 0.5 and s["context_recall"] is True
    s2 = retrieval_scores(Q, [NOISE])
    assert not s2["hit"] and s2["rr"] == 0.0 and s2["context_recall"] is False


def test_correct_and_citation():
    assert is_correct(Q, "Passwords must have at least 14 characters. [1]")
    assert not is_correct(Q, "Passwords must be long. [1]")
    assert citation_ok(Q, [GOOD], 1)
    assert not citation_ok(Q, [NOISE, GOOD], 1), "cited the wrong chunk"
    assert not citation_ok(Q, [GOOD], None)


def test_grounded_catches_an_invented_sentence():
    assert is_grounded("Passwords must have at least 14 characters [1]", [GOOD], 1)
    assert not is_grounded("Passwords must have at least 20 characters [1]", [GOOD], 1), "an invented number must fail"
    assert not is_grounded("Passwords must have at least 14 characters [3]", [GOOD], 3), "citing a chunk that does not exist"


def test_failure_labels():
    ok = Result(Q, "Passwords must have at least 14 characters and multi-factor authentication is mandatory. [1]", 1, [GOOD], False, 0.8)
    assert failure_label(Q, ok) is None
    miss = Result(Q, "Business expenses are reimbursed within 14 days of a complete claim. [1]", 1, [NOISE], False, 0.5)
    assert failure_label(Q, miss).startswith("retrieval failure")
    wrong = Result(Q, "Multi-factor authentication is mandatory. [1]", 1, [GOOD], False, 0.8)
    assert failure_label(Q, wrong).startswith("generation failure")
    refused = Result(Q, REFUSAL, None, [GOOD], True, 0.4)
    assert failure_label(Q, refused).startswith("false refusal")
    oos = RQ("What is the company dress code?", "Not in the documents.", (), None, "out_of_scope")
    assert failure_label(oos, Result(oos, "Something. [1]", 1, [GOOD], False, 0.5)).startswith("hallucination")
    assert failure_label(oos, Result(oos, REFUSAL, None, [GOOD], True, 0.3)) is None


def test_summarise_counts():
    oos = RQ("What is the company dress code?", "Not in the documents.", (), None, "out_of_scope")
    res = [Result(Q, "Passwords must have at least 14 characters and multi-factor authentication is mandatory. [1]", 1, [GOOD], False, 0.8),
           Result(oos, REFUSAL, None, [GOOD], True, 0.3)]
    s = summarise(res)
    assert s["answerable"] == 1 and s["answer_correct"] == 100.0 and s["refused_correctly"] == 1 and s["hallucinated"] == 0 and s["false_refusals"] == 0


# ---- Day 4 additions: unsupported numbers, regression check, LLM answer and judge with a FAKE model (no Ollama needed) ----
from app.rag import compare_to_baseline, unsupported_numbers
from app.rag_llm import build_prompt, judge_supported, parse_answer


def test_unsupported_numbers_catch_an_invented_fact():
    assert unsupported_numbers("Passwords need at least 14 characters [1]", [GOOD], 1) == []
    assert unsupported_numbers("Passwords need at least 20 characters [1]", [GOOD], 1) == ["20"]
    assert unsupported_numbers("Passwords need 14 characters [2]", [GOOD], 2) == ["14"], "a citation to a missing chunk supports nothing"


def test_regression_check_flags_a_drop_and_a_missing_config():
    base = {"C1": {"hit": 93.8, "mrr": 0.938, "context_precision": 52.1, "context_recall": 93.8, "answer_correct": 87.5, "citation_ok": 93.8}}
    same = {"C1": dict(base["C1"])}
    assert compare_to_baseline(base, same) == []
    worse = {"C1": {**base["C1"], "answer_correct": 70.0}}
    assert any("answer_correct fell" in w for w in compare_to_baseline(base, worse))
    assert compare_to_baseline(base, {}) == ["C1: configuration missing"]
    assert compare_to_baseline(base, {"C1": {**base["C1"], "answer_correct": 86.0}}) == [], "a drop inside the tolerance is not a failure"


def test_prompt_and_answer_parsing():
    p = build_prompt("How long must a password be?", [GOOD, NOISE])
    assert "[1] Passwords" in p and "[2] Business" in p and REFUSAL in p
    assert parse_answer("At least 14 characters [1].", 2) == ("At least 14 characters [1].", 1, False)
    assert parse_answer(REFUSAL, 2) == (REFUSAL, None, True)
    assert parse_answer("At least 14 characters [5].", 2)[1] is None, "a citation to a chunk that does not exist is not a citation"


def test_judge_reads_json_and_never_turns_garbage_into_a_pass():
    ctx, ans = GOOD.text, "Passwords need 14 characters [1]"
    assert judge_supported(lambda p: ('{"supported": true, "reason": "ok"}', {}), ctx, ans) is True
    assert judge_supported(lambda p: ('Sure! {"supported": false, "reason": "no"} done', {}), ctx, ans) is False
    assert judge_supported(lambda p: ("I think it is fine.", {}), ctx, ans) is None
    assert judge_supported(lambda p: ('{"supported": "yes"}', {}), ctx, ans) is None, "only a real true or false counts"

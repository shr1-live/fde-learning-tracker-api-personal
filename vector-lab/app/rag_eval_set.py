"""Week 2, Day 4: the RAG evaluation set. 20 questions with the expected answer, the source documents that hold it, the exact
fact (`phrase`) a correct answer must contain, and the profile filter a signed-in user would have (region and year).

Same 12 documents as Day 3 (app/corpus.py). 16 questions have an answer in the documents, 4 do not. Some are hard on purpose:
near-duplicate policies (UK, Germany, archived 2024), a fact that only appears in one of several documents, and a question
about a topic that is close to a real one but not covered (the answer must be a refusal, not a guess).
"""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True)
class RQ:
    question: str
    expected: str  # the expected answer in plain words
    docs: tuple[str, ...]  # doc_ids that hold the answer; empty = not in the documents
    phrase: str | None  # exact text a correct answer (and its source chunk) must contain
    kind: str  # plain | paraphrase | filtered | multi | out_of_scope
    filter: dict = field(default_factory=dict)  # the user's profile filter; used only when the pipeline applies filters


UK25 = {"$and": [{"region": "UK"}, {"year": {"$gte": 2025}}]}
DE = {"region": "Germany"}
US = {"region": "US"}

RAG_QUESTIONS: list[RQ] = [
    # plain: the question uses the document's own words
    RQ("How soon are expense claims reimbursed?", "Within 14 days of a complete claim.", ("expense-all-2025",), "within 14 days", "plain"),
    RQ("After how many minutes does a company laptop lock itself?", "After 5 minutes.", ("remote-all-2025",), "5 minutes", "plain"),
    RQ("How long must a password be?", "At least 14 characters.", ("remote-all-2025",), "14 characters", "plain"),
    RQ("What is the yearly learning budget?", "1,500 euros per year.", ("onboarding-all-2025",), "1,500 euros", "plain"),
    RQ("How often are performance reviews held?", "Twice a year, in June and December.", ("performance-all-2025",), "twice a year", "plain"),
    RQ("How many written quotes does a vendor contract above 5,000 euros need?", "Three written quotes.", ("procurement-all-2025",), "3 written quotes", "plain"),
    # paraphrase: other words for the same fact
    RQ("Can I work from home?", "Yes, up to 3 days a week after probation.", ("remote-all-2025", "remote-de-2025"), "remotely", "paraphrase"),
    RQ("Who signs off on a purchase above 500 euros?", "A director, in writing, before you buy.", ("expense-all-2025",), "director", "paraphrase"),
    RQ("When can I appeal my performance score?", "Within 10 working days, to the next-level manager.", ("performance-all-2025",), "within 10 working days", "paraphrase"),
    RQ("Which days are people expected to be in the office?", "Tuesday and Thursday.", ("remote-all-2025",), "Tuesday and Thursday", "paraphrase"),
    # filtered: near-duplicate policies; the user's region and year pick the right one
    RQ("How many days of paid annual leave do I get?", "24 days (UK, 2025 policy).", ("leave-uk-2025",), "24 days", "filtered", UK25),
    RQ("How many days of paid annual leave do I get?", "30 days (Germany).", ("leave-de-2025",), "30 days", "filtered", DE),
    RQ("What is the hotel cap per night?", "250 dollars in New York and San Francisco, 170 dollars elsewhere (US).", ("travel-us-2025",), "250 dollars", "filtered", US),
    RQ("How many days a week can I work remotely?", "Up to 2 days a week (Germany).", ("remote-de-2025",), "2 days a week", "filtered", DE),
    # multi: the fact sits in one of two similar documents, the filter and the wording both matter
    RQ("How many unused leave days can be carried over?", "10 days (UK, 2025 policy).", ("leave-uk-2025",), "10 unused days", "multi", UK25),
    RQ("When does a medical certificate become necessary?", "From the fourth day of absence (Germany).", ("leave-de-2025",), "fourth day", "multi", DE),
    # out of scope: not in the documents; the correct behaviour is a refusal
    RQ("What is the company dress code?", "Not in the documents.", (), None, "out_of_scope"),
    RQ("How much is the annual bonus?", "Not in the documents.", (), None, "out_of_scope"),
    RQ("When do stock options vest?", "Not in the documents.", (), None, "out_of_scope"),
    RQ("How many days of paid maternity leave do I get in the US?", "Not in the documents (the US has no leave policy here).", (), None, "out_of_scope", US),
]

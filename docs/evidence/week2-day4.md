# Week 2, Day 4 evidence: RAG quality evaluation

**Task:** create an evaluation set of 15-20 questions with expected answers and source documents, run the RAG pipeline, and evaluate retrieval and generated answers separately. Include questions that are not in the documents, document the failures, and show a measurable improvement after at least one optimization.

Code: `vector-lab/app/rag_eval_set.py` (the 20 questions), `app/rag.py` (pipeline and scoring), `run_rag_eval.py` (the run), `tests/test_rag_eval.py` (11 tests). Raw results: `vector-lab/results/rag_eval.json` and `rag_eval.csv`. Step-by-step notebook: `docs/notebooks/week2-day4-rag-evaluation.ipynb`.

Run on a local Postgres 16 with pgvector 0.6.2 and the same 12 documents (17 chunks) as Day 3. **Not tested on Supabase.**

## Important limit: no language model was run
The answer step in every table below is **extractive**: it picks the sentence of the retrieved text that shares most words with the question and cites its chunk as [n]. So these numbers measure retrieval, the filter, the reranker, the gate and a simple answer rule. They do **not** measure how a language model writes, and "grounded 100%" is true by construction (the checker is tested to catch an invented sentence, see `test_grounded_catches_an_invented_sentence`).

A language-model answer step (`app/rag_llm.py`, local Ollama, qwen2.5:3b) and an **LLM judge** for groundedness are written and tested with a fake model (4 tests), and `python run_rag_eval.py --llm` runs them. **They were not run**: when I started Ollama the model failed to load (`failed to allocate buffer of size 1327104000`, the computer had too little free memory). No LLM or judge numbers are reported here.

## The evaluation set
20 questions: 6 plain, 4 paraphrased, 4 that need the user's region or year filter, 2 multi-document, 4 **not in the documents** (dress code, annual bonus, stock options, US maternity leave). Each has the expected answer, the source document(s) and the exact fact a correct answer must contain. Tests check that every expected fact really occurs in its source and that no filter excludes its own source (a bad set would make every score meaningless).

## How retrieval and answer are scored separately
| Stage | Measure | Meaning |
|---|---|---|
| Retrieval | hit | a chunk of a correct document is in the top 3 |
| Retrieval | MRR | how high the first correct chunk sits |
| Retrieval | context precision | share of the 3 chunks that belong to a correct document |
| Retrieval | context recall | the expected fact is somewhere in the 3 chunks |
| Answer | correct | the answer contains the expected fact |
| Answer | grounded | the answer sentence is word for word in the chunk it cites |
| Answer | citation OK | the cited chunk is from a correct document and holds the fact |
| Refusal | refused correctly / hallucinated | answered (or refused) a question that is not in the documents |

Every failed question gets one label: retrieval failure, generation failure, citation failure, false refusal or hallucination.

## Results (16 answerable + 4 not-in-documents, top 3). One change per row.

| Config | Hit | MRR | Ctx precision | Ctx recall | Answer correct | Citation OK | Refused correctly (of 4) | False refusals | Failed questions |
|---|---|---|---|---|---|---|---|---|---|
| C0 baseline: semantic, no filter | 93.8% | 0.823 | 39.6% | 93.8% | **68.8%** | 75.0% | 2 | 0 | 7 |
| C1 + the user's metadata filter | 93.8% | **0.938** | 52.1% | 93.8% | **87.5%** | 93.8% | 2 | 0 | 4 |
| C2 + hybrid search | 93.8% | 0.906 | 52.1% | 93.8% | 87.5% | 93.8% | 1 | 0 | 5 |
| C3 + evidence gate 0.60 | 93.8% | 0.906 | 52.1% | 93.8% | 81.2% | 87.5% | 3 | 1 | 4 |
| C4 + cross-encoder reranker (C1, retrieve 10, keep 3) | **100%** | 0.927 | 52.1% | **100%** | 87.5% | 93.8% | 1 | 0 | 5 |

**Reranker (C4):** it found the one paraphrase question every other setting missed ("Which days are people expected to be in the office?"), so hit and context recall reach 100%. But answer correctness did not improve (the sentence picker still chooses the wrong sentence for some questions), MRR is slightly below C1, it answered more questions that are not in the documents (refused correctly 1 of 4), and **retrieval got about 20 times slower** (median 847 ms against 43 ms, on CPU with the small cross-encoder Xenova/ms-marco-MiniLM-L-6-v2).

### Relevance, latency and cost (same run)
| Config | Answer relevance | Context relevance | Retrieval ms (median) | Tokens |
|---|---|---|---|---|
| C0 baseline | 0.697 | 0.658 | 49.7 | 0 |
| C1 + filter | 0.692 | 0.630 | 42.9 | 0 |
| C2 + hybrid | 0.678 | 0.623 | 44.4 | 0 |
| C3 + gate | 0.699 | 0.623 | 44.3 | 0 |
| C4 + rerank | 0.678 | 0.623 | 847.1 | 0 |

Answer relevance is the embedding similarity between the question and the answer; context relevance is the average similarity between the question and the retrieved chunks (both 0 to 1). They barely move here, so they do not tell these configurations apart. Cost: nothing is billed (local embedding, no language model), so tokens are 0. With a hosted model, cost per question is (prompt tokens + output tokens) times the provider's price; `--llm` records those tokens. Embedding a question costs about 40 ms; the reranker adds about 800 ms.

### Regression check
`python run_rag_eval.py --save-baseline` stores the headline numbers (`results/rag_eval_baseline.json`); `python run_rag_eval.py --check` re-runs and exits with an error if hit, recall, correct answers, citations or MRR fall by more than 2 points. It passed against the saved baseline. The comparison is tested (`test_regression_check_flags_a_drop_and_a_missing_config`).

(Grounded was 100% in every row, true by construction, see the limit above. The gate threshold 0.60 was fixed from the Day 3 evaluation before this run, not tuned on these 20 questions.)

**Measurable improvement:** the metadata filter (C0 to C1) raised correct answers from 68.8% to 87.5% (+18.7 points), citation accuracy from 75.0% to 93.8%, MRR from 0.823 to 0.938, and cut failed questions from 7 to 4. The three "annual leave", "remote days" and "medical certificate" questions went wrong at baseline because near-duplicate policies from other regions ranked first; the user's profile filter fixed them.

**Not an improvement:** hybrid search (C2) did not help (MRR fell to 0.906, and one more not-in-documents question got an answer). The gate (C3) refused 3 of the 4 questions that are not in the documents (up from 2) but also refused one answerable question (medical certificate) and lowered correct answers to 81.2%. The gate is a trade-off, not a fix.

## Failure analysis (what is still wrong in the best row, C1)
| Question | Label | Why | Improvement note |
|---|---|---|---|
| When can I appeal my performance score? | generation failure | The right chunk was retrieved, but the extractive rule picked the sentence that shares the word "performance", not the sentence about the second review | A language model, or a sentence reranker, should choose the sentence. Not built |
| Which days are people expected to be in the office? | retrieval failure | Paraphrase: no shared words with "Tuesday and Thursday"; semantic search ranked holiday and leave chunks first | A reranker, or a question-rewriting step. Not built |
| How much is the annual bonus? | hallucination | Question not in the documents, but "annual" matches the leave policy, so a sentence was returned | A gate that looks at the sentence overlap as well as the chunk similarity; or a language model told to refuse |
| US maternity leave | hallucination | Close to a real topic (travel policy for the US), best similarity above the 0.60 gate | Same as above. A threshold alone cannot separate these |

## Coverage of the sheet's Day 4 topics
| Topic on the sheet | Where it is covered |
|---|---|
| Why RAG evaluation is needed | Notion page, explanation and glossary |
| Separating retrieval failures from generation failures | Built: failure labels per question |
| Groundedness, faithfulness, answer relevance | Built: `is_grounded`, `unsupported_numbers`, `answer_relevance` (embedding similarity) |
| Context relevance, precision, recall | Built: `context_relevance`, `retrieval_scores` |
| Correctness and quality of citations | Built: `is_correct`, `citation_ok` |
| Building representative evaluation datasets | Built: 20 questions, validated by tests |
| Measuring retrieval and answer quality | Built: the tables above |
| Evaluation frameworks and LLM-as-a-judge | Concepts in Notion (RAGAS, TruLens, DeepEval). Judge **written and tested with a fake model, not run** |
| Reranking and other optimizations | Built and measured: metadata filter, hybrid search, evidence gate, cross-encoder reranker |
| Detecting hallucinations and unsupported answers | Built: not-in-documents questions, number check, groundedness check |
| Failure analysis, regression testing, iterative improvement | Built: failure table, baseline and `--check` |
| Latency and cost | Measured: retrieval latency; tokens 0 (no model run) |

## What is not done
- The language-model answer step and the LLM judge were **not run** (see the limit above), so there is no real test of a model's wording, no judge agreement number, and no token cost.
- Not run on Supabase. The 16-question set is small: one question is 6.25 points, so differences of one question are not strong evidence.
- Hybrid search was not tuned (its keyword side is Postgres full-text ranking, as on Day 3). Query rewriting and chunk-size tuning were not tried.

## Acceptance points
- Reusable evaluation dataset: `app/rag_eval_set.py`, validated by tests.
- Retrieval and answer evaluated separately: tables above.
- Groundedness and citation accuracy checked: `is_grounded`, `citation_ok`.
- Retrieval-related and generation-related failures identified: labels per question in `results/rag_eval.json`.
- Questions not in the documents included: 4 of 20.
- Results, failure analysis and improvement notes: this page.
- Measurable improvement after an optimization: C0 to C1.

**Tests:** 63 pass in `vector-lab/tests` (the 48 from Day 3 and 15 new: the evaluation set, the scoring, the regression check, and the language-model answer step and judge with a fake model).

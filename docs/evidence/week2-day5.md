# Week 2, Day 5 evidence: end-to-end document assistant

**Task:** a working document assistant that accepts uploaded documents, answers with citations, refuses when the documents do not say, supports follow-up questions, enforces who may read what, logs requests, and has functional and security tests.

Code: `vector-lab/app/assistant.py` (logic), `vector-lab/app/server.py` (HTTP), `vector-lab/tests/test_assistant.py` (18 tests), portal tab `Practice lab > Ask your documents` (`src/pages/DocQA.tsx` in the web repo). Debugging guide: `vector-lab/DEBUGGING.md`. Step-by-step notebook: `docs/notebooks/week2-day5-document-assistant.ipynb` (runs the real file-reading, screening, access and follow-up code on an in-memory store; its code cells were run on my computer, not opened in Colab itself).

Built on the Day 3 store (Postgres + pgvector) and the Day 4 pipeline. Tested on a local Postgres 16 with pgvector. **Not tested on Supabase. Not deployed.**

## What it does
| Step | Behaviour |
|---|---|
| Upload | `.txt`, `.md`, `.pdf`, `.docx`, up to 2 MB. Text is extracted, cleaned, screened for hidden instructions, chunked, embedded and stored with department, region, year and visibility |
| Ask | Hybrid search, evidence gate (default similarity 0.60), answer with a citation like [1] |
| Refuse | If the documents do not say, the answer is a refusal with a reason |
| Follow-up | A short continuation such as "and in Germany?" is completed from the last question; the completed question is shown to the user |
| Remove | A person can remove their own documents |

## Security rules (enforced by the server, not the page)
| Rule | Test |
|---|---|
| One person's documents are never visible to another, and a foreign document looks like a missing one (404) | S01, S12 |
| A department rule hides other departments; a caller's filter can only narrow, never widen | S02, S03 |
| Uploading to a department you are not in is refused (403) | S04 |
| A file that contains orders for a model is flagged at upload and never used as context | S05, S06 |
| An attack in the question cannot change the answer rules; an answer is only a sentence from a source | S07 |
| Hostile filters, settings and SQL-looking keys are refused with 400 and change nothing | S08 |
| A model answer with an invented number or no citation is replaced by a refusal; a model outage returns 503 without leaking the cause | S09 |
| Logs hold ids, timings and outcomes, not the question text (unless `VECTOR_LAB_LOG_TEXT=1`) | S10 |
| Wrong or missing key gives 401 with a request id; errors share one shape | S12 |

To check that these tests can fail, I broke the department rule on purpose (two tests failed) and the flagged-file rule (one test failed), then restored the code.

## Results
- **Tests: 81 pass** in `vector-lab/tests` (48 from Day 3, 15 from Day 4, 18 new).
- **Browser run (partial):** the tab loaded, connected to a service, loaded the 12 sample documents and answered "After how many minutes does a company laptop lock itself?" correctly with a citation. It found a real bug: a new question ("What is the company dress code?") was glued to the previous one by the follow-up rule and got a wrong answer. Fixed; a test now covers it (`test_A06_pure_helpers`).
- **Web checks:** `npx tsc --noEmit` and `npm run build` passed.

## What is not done
- The browser run did **not** finish after the fix: the later steps (upload of a clean file and an attack file, the 2 MB limit, dark mode, phone width) were not re-run. Light, dark and phone layouts were **not** checked.
- No language model was run (the machine had too little memory for Ollama). Answers are extractive. The model path and its verification are tested with a fake model only.
- Scanned PDFs need OCR, which is not built. Streaming answers are not built. Conversation history is kept in the page, not on the server.
- Single-user local mode is the default; per-person keys come from `VECTOR_LAB_USERS` and have only been tested in code and over HTTP in tests.
- Not deployed and not tested on Supabase.
- No demo video for Day 4 or Day 5.

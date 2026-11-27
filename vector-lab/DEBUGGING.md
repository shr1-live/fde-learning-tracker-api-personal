# Debugging vector-lab by hand

Start at the symptom, go to the step, run the one command. Every step can be run alone in a Python shell:
`cd vector-lab` then `.venv\Scripts\python.exe` (Windows).

## The path of one question (Day 5)

`POST /assistant/ask` goes through these steps, in this order. Each is one function.

| # | Step | Function (file) | Look here if... |
|---|------|-----------------|-----------------|
| 1 | Who is calling | `identify` (app/server.py) | you get 401 |
| 2 | Check the settings | `Assistant.ask` start (app/assistant.py) | you get 400 `bad_question`, `bad_k`, `bad_min_score` |
| 3 | Complete a follow-up | `rewrite_followup` (app/assistant.py) | "and in Germany?" is not completed, or a new question is glued to the old one. The response shows `standalone_question` |
| 4 | Build the access filter | `answer_filter`, `visible_filter` (app/assistant.py) | someone sees a document they should not, or sees none |
| 5 | Search | `VectorLab.search` (app/engine.py) | wrong chunks come back |
| 6 | Evidence gate | `best < min_score` in `Assistant.ask` | a good question is refused, or a bad one is answered |
| 7 | Write the answer | `extract_answer` (app/rag.py) or the model path | the right chunk is found but the sentence is wrong |
| 8 | Verify (model path only) | `unsupported_numbers` (app/rag.py) | an answer is replaced by "could not be verified" |
| 9 | Log | `RequestLog.record` (app/assistant.py) | check `data/requests.jsonl` |

Every response has an `X-Request-Id` header. Search `data/requests.jsonl` for it to see the outcome, timings and error code of that call.

## Quick checks

- **Is the service up?** `curl http://localhost:8100/health` gives `{"ok":true}`.
- **Is it the new code?** `curl http://localhost:8100/assistant/me` must not be 404. A 404 means an old copy still holds the port: `netstat -ano | findstr :8100`.
- **What is stored?** In a shell: `from app.db import database_url; from app.engine import VectorLab; lab = VectorLab(database_url()); print(lab.stats())`.
- **See step 5 alone:** `lab.search("your question", "hybrid", 3, None)`; each hit has `.doc_id`, `.sem` (similarity) and `.text`.
- **See step 4 alone:** `from app.assistant import Caller, answer_filter; print(answer_filter(Caller("o", ("hr",)), None))`.
- **See step 3 alone:** `from app.assistant import rewrite_followup; rewrite_followup("and in Germany?", [{"role": "user", "content": "How many days of leave?"}])`.
- **Upload problems:** `from app.assistant import extract_text, screen_text`; run them on the file's bytes. `extract_text` gives the status and code, `screen_text` lists the patterns that flagged it.

## Errors you may see

| Code | Meaning | Fix |
|------|---------|-----|
| 401 `unauthorized` | wrong or missing `X-Api-Key` | check `VECTOR_LAB_USERS` |
| 403 `department_not_allowed` | uploading to a department you are not in | use your own department |
| 404 `not_found` | no such document, or it belongs to someone else (on purpose, the same answer) | list your documents first |
| 413 `too_large` | file over 2 MB | split the file |
| 415 `unsupported_type` | not .txt, .md, .pdf or .docx | convert it |
| 422 `no_text` | scanned PDF, no text layer (OCR is not built) | use a text PDF |
| 503 `model_unavailable` | the language model could not be reached | start Ollama, or leave the model off |

## Known machine limit

This computer is short on memory. If tests fail with `bad allocation` or `Load model ... FAIL`, close other programs and stop extra copies of the service, the preview server and Ollama, then run again:
`.venv\Scripts\python.exe -m pytest tests -q -p no:cacheprovider`
Expected: 81 passed.

## Run order for the checks

1. `.venv\Scripts\python.exe -m pytest tests -q -p no:cacheprovider` (all tests)
2. `.venv\Scripts\python.exe run_rag_eval.py --check` (Day 4 regression check)
3. In the web repo: `npx tsc --noEmit`, then `npm run build`

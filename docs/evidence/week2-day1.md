# Week 2, Day 1 evidence: RAG architecture

**Task:** build a basic RAG pipeline: ingest a small set of approved documents and expose a query API or interface.

**Acceptance criteria:** ingest and process multiple documents; keep ingestion, retrieval and generation as clear stages; generate answers using retrieved context; return source references or citations; provide a fallback when the documents do not contain sufficient evidence; document the architecture and data flow.

## The flow, end to end

1. **Add documents** (Practice lab, Document assistant tab, or `POST /api/rag/documents`): title, text and optional tags, 20 to 20,000 characters each, at most 20 per person. `POST /api/rag/sample` loads four sample handbook documents. Documents are private to the person who adds them ([RagService](../../FdeTracker.Api/Services/RagService.cs)).
2. **Ingestion:** the text is stored with its title and tags as the source, then cut into chunks that keep the document, their part number and the tags ([Chunker](../../FdeTracker.Api/Services/Rag/RagEngine.cs)).
3. **Retrieval** (`POST /api/rag/ask`): the question and every chunk become vectors and the best K chunks are chosen by vector similarity, by keywords (BM25) or both merged ([Retriever](../../FdeTracker.Api/Services/Rag/RagEngine.cs)).
4. **Generation:** a model, when one is configured, is told to answer only from the numbered chunks and cite them. Its answer is accepted only if every `[n]` is a real source; otherwise the best sentences are quoted ([Answerer](../../FdeTracker.Api/Services/Rag/RagEngine.cs), `RagService.Ask`).
5. **Fallback:** when no chunk covers the question the reply is "I could not find this in the approved documents."

Architecture and data-flow diagram: [WEEK2-RAG-ARCHITECTURE.md](https://github.com/shr1-live/fde-learning-tracker-webapp-personal/blob/feature/week2-rag/docs/WEEK2-RAG-ARCHITECTURE.md) in the web repository.

## Result: one question, its passages and its answer

Paragraph chunks, hybrid search, Top K 3, on the four sample documents (12 chunks).

| | |
|---|---|
| Question | After how many minutes does a company laptop lock itself? |
| Retrieved 1 | Remote work and security, part 2 (score 0.0328): "Company laptops must use full disk encryption and lock automatically after 5 minutes. ..." |
| Retrieved 2 | Onboarding and training, part 3 (score 0.0323) |
| Retrieved 3 | Remote work and security, part 1 (score 0.0317) |
| **Answer** | Company laptops must use full disk encryption and lock automatically after 5 minutes. [1] |
| Cited | Remote work and security, part 2 |

Out-of-scope question: **How much is the annual bonus?** The retrieval returns the nearest chunks (the leave policy, because of "annual"), but no sentence covers the question, so the reply is **"I could not find this in the approved documents."** with no citations.

## How each criterion is met

| Criterion | Where |
|---|---|
| Ingest and process multiple documents | Four sample documents, or your own; text is stored and chunked ([RagService.Add and LoadSample](../../FdeTracker.Api/Services/RagService.cs)); test `R01_a_person_adds_loads_asks_and_removes_documents` |
| Three separate stages | `Chunker` (ingestion), `Retriever` (retrieval), `Answerer` and `RagService.Ask` (generation), each testable alone: tests `E01`, `E02`, `E03` in [RagTests](../../FdeTracker.Tests/RagTests.cs) |
| Answers from retrieved context | The answer is built only from the retrieved chunks; the model prompt carries only those chunks, as data ([RagService](../../FdeTracker.Api/Services/RagService.cs)); test `R04` |
| Source references | Every answer lists the cited source: document, part number and text; the response also lists everything retrieved |
| Fallback | `Answerer.Refusal`; tests `E03` and `R01`; the evaluation checks it on 2 questions the documents cannot answer: refused 2 of 2 |
| Architecture and data flow documented | The diagram linked above |

## What is not done

- Only plain text is ingested (pasted, or a `.txt` or `.md` file). PDF and DOCX extraction is not built.
- The embedding is a local hashed one, not a trained model: it matches shared words, not meaning.
- Without a configured model the answer quotes sentences instead of being written.

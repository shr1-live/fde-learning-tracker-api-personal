# Week 2, Day 2 evidence: chunking and embeddings

**Task:** implement at least two chunking strategies and generate embeddings for the same document set. Compare retrieval results using a fixed set of 10 to 15 questions.

**Acceptance criteria:** two or more configurable chunking strategies; configurable chunk size and overlap where applicable; preserve document metadata and source references; generate and store embeddings; compare retrieval relevance across strategies; explain the selected strategy with evidence.

## What was built ([RagEngine](../../FdeTracker.Api/Services/Rag/RagEngine.cs))

| Part | Detail |
|---|---|
| Strategy `paragraph` | One chunk per paragraph; a paragraph longer than the maximum is cut at sentence ends. Maximum size is a setting (80 to 1000 characters, default 420). |
| Strategy `fixed` | Windows of a set length that start and end at word boundaries, with overlap. Size 80 to 1000 characters (default 260), overlap 0 to half the size (default 60). |
| Settings | `strategy`, `chunk_size` and `overlap` on `POST /api/rag/ask`, `POST /api/rag/search` and `GET /api/rag/eval`; the same fields are in the Document assistant tab. Wrong values are refused with a message. |
| Metadata | Every chunk carries its document title, part number and tags. Printed example: document "Leave policy", part 2, tags [hr], 259 characters, starting "expires on 31 March. Sick leave is separate from annual leave ...". |
| Embeddings | Words, word pairs and word pieces hashed into 256 numbers, scaled to length 1. Made once for each distinct text and kept in memory for reuse (`HashEmbedder.EmbedStored`); test `E08_embeddings_are_stored_and_reused` shows the store grows by one on first use and a second request returns the same stored vector. The store is lost on a restart. |

## Two settings of the same strategy (fixed windows, hybrid search, Top K 3)

| Chunk size | Overlap | Chunks made | Retrieval hit | MRR | Answer correct |
|---|---|---|---|---|---|
| 120 | 20 | 24 | 100% | 0.969 | 62.5% |
| 260 | 60 | 12 | 100% | 0.969 | 68.8% |
| 400 | 100 | 8 | 100% | 0.969 | 68.8% |

Very small chunks cut a fact away from its context, so the answer step finds fewer complete sentences (62.5%).

## Strategy comparison (18 questions: 16 answerable, 2 not in the documents)

Best-known rows; all 18 settings are in [WEEK2-RAG-EVIDENCE.md](https://github.com/shr1-live/fde-learning-tracker-webapp-personal/blob/feature/week2-rag/docs/WEEK2-RAG-EVIDENCE.md) and in the app (button "Compare all 18 settings", with a CSV download).

| Chunking | Search | Top K | Retrieval hit | MRR |
|---|---|---|---|---|
| fixed | semantic | 1 | 93.8% | 0.938 |
| paragraph | semantic | 1 | 87.5% | 0.875 |
| fixed | hybrid | 3 | 100% | 0.969 |
| paragraph | hybrid | 3 | 100% | 0.969 |
| paragraph | keyword | 1 | 87.5% | 0.875 |

## The strategy chosen, and why

**Paragraph chunks with hybrid search and Top K 3.** It finds the right document for all 16 answerable questions (100%, MRR 0.969), and refuses both questions the documents do not answer. Fixed windows tie with it at this setting, so the numbers do not separate the strategies here. Paragraph was picked because its chunks follow the document's own structure (3 per document, each a complete policy), so a cited source reads as a whole statement. Fixed windows were slightly better at Top K 1 with vector search alone (93.8% against 87.5%), which is why K 3 and the hybrid are used.

## What is not done

- The question set is 18 questions (16 answerable), above the 10 to 15 the task names; it is the same set Day 4 uses.
- Sizes are in characters, not tokens. Recursive, semantic and structure-aware chunking (headings, tables) are not built.
- The embedding is a local hashed one, not a trained model; "Embedding model selection" is therefore not compared. Re-embedding on change happens by itself because the store is keyed by the text.
- The evaluation uses four invented handbook documents, not real company documents.

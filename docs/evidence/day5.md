# Day 5 evidence: end-to-end LLM application

**Task:** deliver an AI-powered recruitment assistant that analyses supplied candidate profiles against a job description, returns structured, evidence-based output, and uses at least two controlled tools or application functions.

**Acceptance criteria:** working end-to-end flow; structured and validated response; at least two tools/functions; input/output validation; failure handling; basic logs and token/latency tracking; tests, documentation, and a demo.

## The flow, end to end

1. **Web app** (the Assistant page in the FDE Learning Tracker): the person pastes a job description and up to five candidate profiles, picks a prompt version, and sees how many calls they have left today.
2. **API** `POST /api/assistant/analyze` ([AssistantController](../../FdeTracker.Api/Controllers/AssistantController.cs)): the request is validated before anything else (job description 20 to 6000 characters, 1 to 5 candidates, no empty entries, prompt version v1 to v7) and the person must be signed in and approved.
3. **Service** ([AssistantService](../../FdeTracker.Api/Services/AssistantService.cs)): reserves the call first, under a per-person lock, so the daily cap holds even for parallel or cancelled requests; builds the prompt from the stored, versioned prompt ([PromptLibrary](../../FdeTracker.Api/Services/Llm/PromptLibrary.cs)).
4. **Model client** ([LlmClient](../../FdeTracker.Api/Services/Llm/LlmClient.cs), [AnthropicClient](../../FdeTracker.Api/Services/Llm/AnthropicClient.cs)): any chat-completions provider (Gemini, OpenAI, a local Ollama model) or Claude, chosen by settings; the key lives only in server environment variables.
5. **Checks before anyone sees the answer** ([AnalysisPipeline](../../FdeTracker.Api/Services/Llm/AnalysisPipeline.cs), [AnalysisParser](../../FdeTracker.Api/Services/Llm/AnalysisParser.cs), [Guardrails](../../FdeTracker.Api/Services/Llm/Guardrails.cs)): the reply must parse into the fixed JSON shape; every claim needs a quote that is really in the profile; a rating needs evidence; hidden instructions in a profile are not obeyed. An answer that fails is not shown and is logged as `invalid_output`.
6. **Result:** per candidate a rating, matched and missing skills, a match score, the evidence quotes and a short summary, plus the model, tokens and time taken.

## The two controlled tools (application functions)

Both are plain code in [SkillTools](../../FdeTracker.Api/Services/Llm/SkillTools.cs); the model cannot change what they return.

| Function | What it does | Why it is code, not the model |
|---|---|---|
| `ExtractSkills(text)` | Finds known skills in a job description or a profile, from a fixed vocabulary | The same text always gives the same skills, so "strong match" cannot be invented |
| `ScoreMatch(required, found)` | Returns matched skills, missing skills and a 0-100 score | The score is arithmetic, checkable by anyone |

The task assistant (Day 3) adds tools the model may ask for, while the code decides: it asks for one change, and deleting needs a confirmation.

## Failure handling

| Situation | What the person sees |
|---|---|
| Model not configured | 503 "The assistant is not set up yet" |
| Daily cap reached | 429 with the cap, counted per person per day |
| Provider rate limit (429) or 5xx, network error | Retried with jitter (up to 3 attempts, honouring Retry-After up to 30 s, 60 s overall); a longer wait is shown as "try again in N s" |
| Answer is not valid JSON or fails the checks | A clear error; the bad answer is never shown; logged as `invalid_output` |
| Bad input | 400 with the field that is wrong |

Error categories are in [LlmFailure](../../FdeTracker.Api/Services/Llm/LlmFailure.cs) and [Errors](../../FdeTracker.Api/Services/Llm/Errors.cs).

## Logs, tokens and latency

Every call writes one row to `llm_calls` ([LlmCallLedger](../../FdeTracker.Api/Services/Llm/LlmCallLedger.cs)): who, prompt version, model, input tokens, output tokens, latency in ms and the outcome (`ok`, `invalid_output`, provider error). The same numbers come back in the response and are shown on the page; the daily cap is counted from this table.

## Tests, documentation, demo

- **Tests:** the whole backend suite, 247 tests, all passed on 2026-11-23 (`dotnet test`, with a real Postgres in a container), covering validation, the daily cap, provider failures and retries, the parser, the guardrails and the two functions.
- **Documentation:** [README](../../README.md) (setup, settings, errors), [Demo script](../DEMO.md) (about 10 minutes, seven parts), [Release plan](../RELEASE-PLAN.md), [Week 1 report](../WEEK1-REPORT.md), [Architecture](../ARCHITECTURE.md).
- **Demo:** the script is ready; the recorded video is not done yet.

## How each criterion is met

| Criterion | Met? | Where |
|---|---|---|
| Working end-to-end flow | Yes | Assistant page › API › model › checks › result |
| Structured and validated response | Yes | Fixed JSON shape, parser and guardrails; failures never shown |
| At least two tools/functions | Yes | `ExtractSkills`, `ScoreMatch` (plus the Day 3 task-assistant tools) |
| Input/output validation | Yes | Request validation; output parser and guardrails |
| Failure handling | Yes | Table above |
| Logs, token and latency tracking | Yes | `llm_calls` per call; shown in the response |
| Tests | Yes | Whole backend suite, see above |
| Documentation | Yes | README, demo script, release plan, report |
| Demo | Partly | Script ready; video not recorded |

## Against the FDE Readiness Guide

| Area | Evidence | Assessment |
|---|---|---|
| Problem understanding | The task became concrete rules: what the output must contain, what may never be shown, what each failure looks like to the person | Good |
| Technical decisions | Hard checks in code instead of trusting the prompt; scoring as plain functions; any provider behind one interface; key only on the server | Good |
| Implementation | Full flow working, with the daily cap, logging and guardrails used by the live API | Solid |
| Testing and reliability | Automated tests across the backend; Day 4's 30 + 15 evaluation cases run against this same pipeline | Good; hosted-model runs still thin |
| Delivery and communication | README, demo script, release plan and this page; limits stated plainly | Good; the video is missing |

**Suggested Rate: 4.** The assistant works end to end with validated output, two controlled functions, failure handling, logging and tests, and I built and debugged it myself. It is not a 5 because the demo video is not recorded and it has not been run end to end on the deployed site with a hosted model yet (release plan phases F and G).

## Not done

- Demo video (release plan phase G).
- A full run on the deployed site with a hosted model (release plan phase F).
- Streaming responses: the answer comes back in one piece, with a loading state.
- Cost in money: tokens are recorded, but no paid model was used, so there is no cost figure.

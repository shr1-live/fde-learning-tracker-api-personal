"""Week 2, Day 4: a language-model answer step and an LLM judge, both on the local Ollama service (127.0.0.1:11434). No key, no cost.

Same three stages as app/rag.py. Only the generate stage changes: the model writes the answer from the numbered chunks and must cite them.
The judge is a second call to a model that reads the context and the answer and says whether every claim is supported. A judge is itself a
model, so it can be wrong: run_rag_eval.py compares its verdicts with the plain-code checks and reports how often they agree.

`ask` is a parameter everywhere, so tests use a fake model and nothing here needs Ollama to be tested.
"""
from __future__ import annotations

import json
import re
import time
import urllib.request
from typing import Callable

from .engine import Hit, VectorLab
from .rag import REFUSAL, Config, Result, retrieve
from .rag_eval_set import RQ

OLLAMA = "http://127.0.0.1:11434"
Ask = Callable[[str], tuple[str, dict]]  # prompt -> (text, {"prompt_tokens", "output_tokens"})


def ollama_ask(model: str = "qwen2.5:3b", num_predict: int = 160, timeout: int = 600) -> Ask:
    def ask(prompt: str) -> tuple[str, dict]:
        body = json.dumps({"model": model, "prompt": prompt, "stream": False, "options": {"temperature": 0, "num_predict": num_predict}}).encode()
        req = urllib.request.Request(f"{OLLAMA}/api/generate", data=body, headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            d = json.load(r)
        return d.get("response", "").strip(), {"prompt_tokens": d.get("prompt_eval_count", 0), "output_tokens": d.get("eval_count", 0)}
    return ask


def build_prompt(question: str, hits: list[Hit]) -> str:
    ctx = "\n".join(f"[{n}] {h.text.strip()}" for n, h in enumerate(hits, start=1))
    return ("Answer the question using ONLY the numbered context. Write one short sentence and cite the chunk number like [1]. "
            f"If the context does not contain the answer, reply exactly: {REFUSAL}\n\nContext:\n{ctx}\n\nQuestion: {question}\nAnswer:")


def parse_answer(text: str, n_hits: int) -> tuple[str, int | None, bool]:
    """(answer, cited chunk number, refused). A citation to a chunk that does not exist counts as no citation."""
    text = text.strip()
    if REFUSAL.lower().rstrip(".") in text.lower():
        return REFUSAL, None, True
    m = re.search(r"\[(\d+)\]", text)
    cited = int(m.group(1)) if m and 1 <= int(m.group(1)) <= n_hits else None
    return text, cited, False


def answer_llm(lab: VectorLab, q: RQ, cfg: Config, ask: Ask) -> Result:
    hits, best_sim, ms = retrieve(lab, q, cfg)
    if cfg.gate is not None and (best_sim is None or best_sim < cfg.gate):
        return Result(q, REFUSAL, None, hits, True, best_sim, retrieve_ms=ms)
    t0 = time.perf_counter()
    text, usage = ask(build_prompt(q.question, hits))
    gen_ms = (time.perf_counter() - t0) * 1000
    ans, cited, refused = parse_answer(text, len(hits))
    return Result(q, ans, cited, hits, refused, best_sim, retrieve_ms=ms, gen_ms=gen_ms, prompt_tokens=usage.get("prompt_tokens", 0), output_tokens=usage.get("output_tokens", 0))


JUDGE = ("You check whether an answer is supported by a context.\n"
         "Context:\n{context}\n\nAnswer: {answer}\n\n"
         "Is EVERY claim in the answer supported by the context? Reply with JSON only: {{\"supported\": true or false, \"reason\": \"one short sentence\"}}")


def judge_supported(ask: Ask, context: str, answer_text: str) -> bool | None:
    """LLM-as-a-judge for groundedness. None when the judge's reply cannot be read (counted as no verdict, never as a pass)."""
    text, _ = ask(JUDGE.format(context=context, answer=re.sub(r"\s*\[\d+\]", "", answer_text)))
    m = re.search(r"\{.*\}", text, re.S)
    if not m:
        return None
    try:
        v = json.loads(m.group(0)).get("supported")
    except ValueError:
        return None
    return v if isinstance(v, bool) else None

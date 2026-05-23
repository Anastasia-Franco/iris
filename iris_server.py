#!/usr/bin/env python3
"""
IRIS API Server — FastAPI service centralizing all IRIS intelligence on the Ubuntu node.

Run with:
    uvicorn iris_server:app --host 0.0.0.0 --port 8000

Environment variables (can also be set in .env):
    IRIS_OLLAMA_HOST       — default: http://127.0.0.1:11434
    IRIS_DEFAULT_MODEL     — default: qwen3:30b
    IRIS_DB_PATH           — default: /opt/iris/data/iris_memory.db
    IRIS_IDENTITY_FILE     — default: /opt/iris/iris_identity.txt
    IRIS_MAX_CONTEXT_TOKENS — default: 6000
"""

import asyncio
import hashlib
import json
import logging
import os
import re
import threading
import time
import uuid

import requests
from dotenv import load_dotenv
from fastapi import BackgroundTasks, FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import PlainTextResponse, StreamingResponse
from pydantic import BaseModel

import iris_memory

load_dotenv()

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger("iris")

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

OLLAMA_HOST        = os.environ.get("IRIS_OLLAMA_HOST",         "http://127.0.0.1:11434")
DEFAULT_MODEL      = os.environ.get("IRIS_DEFAULT_MODEL",       "qwen3:30b")
DB_PATH            = os.environ.get("IRIS_DB_PATH",             "/opt/iris/data/iris_memory.db")
IDENTITY_FILE      = os.environ.get("IRIS_IDENTITY_FILE",       "/opt/iris/iris_identity.txt")
MAX_CONTEXT_TOKENS = int(os.environ.get("IRIS_MAX_CONTEXT_TOKENS", "6000"))

# PDF ingestion
PDF_CACHE_DIR = os.environ.get("IRIS_PDF_CACHE_DIR", "/opt/iris/data/pdf_cache")
try:
    import fitz  # pymupdf
    _PYMUPDF_AVAILABLE = True
except ImportError:
    _PYMUPDF_AVAILABLE = False
    logger.warning("pymupdf not installed — PDF ingestion will be unavailable. Run: pip install pymupdf")

# Research orchestration limits
MAX_RESEARCH_SUBQUERIES  = int(os.environ.get("IRIS_MAX_RESEARCH_SUBQUERIES", "5"))
MAX_TAVILY_QUERY_LEN     = 380   # Tavily hard-limits queries to ~400 chars; leave headroom
MAX_RESULTS_PER_SUBQUERY = int(os.environ.get("IRIS_MAX_RESULTS_PER_SUBQUERY", "5"))

# Per-session debug store: session_id → last context debug dict
_last_context_debug: dict[str, dict] = {}

# ---------------------------------------------------------------------------
# App
# ---------------------------------------------------------------------------

app = FastAPI(title="IRIS API", version="2.0.0")

# ---------------------------------------------------------------------------
# Pydantic models
# ---------------------------------------------------------------------------

class ChatRequest(BaseModel):
    session_id: str | None = None
    project_id: str | None = None
    prompt: str
    model: str = DEFAULT_MODEL
    mode: str = "prompt"          # "prompt" | "research"

class PromoteRequest(BaseModel):
    content: str
    scope: str = "global"         # operator | project | global | research | session
    state: str = "durable"        # ephemeral | session | durable | pinned | archived | deleted
    project_id: str | None = None
    tags: str | None = None
    confidence: float = 1.0
    source: str | None = None
    generated_by_model: str | None = None

class SeedRequest(BaseModel):
    filename: str
    content: str
    project_id: str | None = None

class ProjectRequest(BaseModel):
    name: str
    description: str | None = None

class ProjectUpdateRequest(BaseModel):
    name: str | None = None
    description: str | None = None
    status: str | None = None     # active | archived | paused

class IngestRequest(BaseModel):
    project_id:     str
    filename:       str
    content:        str
    doc_type:       str = "markdown"
    authority_level: str = "Informational"   # Definitive|Authoritative|Informational|Contextual|Anecdotal
    document_type:  str = "other"            # published_framework|operational_guide|strategic_draft|meeting_notes|planning_discussion|other
    finality:       str = "final"            # final|draft|provisional

class DocumentMetadataUpdate(BaseModel):
    authority_level: str | None = None
    document_type:   str | None = None
    finality:        str | None = None

class DocumentProjectUpdate(BaseModel):
    project_id: str

class NoteUpdateRequest(BaseModel):
    content: str | None = None
    scope: str | None = None
    state: str | None = None
    confidence: float | None = None
    tags: str | None = None

class ResearchPromoteRequest(BaseModel):
    selected_notes: list[dict]    # list of {content, scope, state, tags}

# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _load_system_identity() -> str:
    """Load the permanent system identity from file, env var, or built-in fallback.
    Re-read on every request so edits take effect without a server restart."""
    if os.path.isfile(IDENTITY_FILE):
        with open(IDENTITY_FILE, "r", encoding="utf-8") as fh:
            return fh.read().strip()
    env_val = os.environ.get("IRIS_SYSTEM_IDENTITY", "").strip()
    if env_val:
        return env_val
    return (
        "You are IRIS (Independent Resilient Intelligence System), "
        "a local AI assistant running on private hardware owned by Stacey."
    )


def _get_db():
    return iris_memory.init_db(DB_PATH)


def _apply_budget(text: str, max_tokens: int) -> tuple[str, int]:
    """Truncate text to fit within a token budget (1 token ≈ 4 chars).
    Returns (possibly-truncated text, estimated token count)."""
    max_chars = max_tokens * 4
    if len(text) <= max_chars:
        return text, len(text) // 4
    return text[:max_chars] + "\n… [truncated to fit context budget]", max_tokens


def _embed_in_background(message_id: str, text: str, project_id: str = None) -> None:
    """Store an embedding for a saved message. Runs in a daemon thread."""
    try:
        db = iris_memory.init_db(DB_PATH)
        iris_memory.embed_and_store(db, message_id, "message", text, OLLAMA_HOST,
                                    project_id=project_id)
        db.close()
    except Exception:
        pass  # never surface embedding failures to the caller


def _embed_note_in_background(note_id: str, content: str, project_id: str = None) -> None:
    """Embed a promoted memory note with source_type='memory_note'.
    Enables semantic relevance filtering of project notes in Layer 3 of _build_context()."""
    try:
        db = iris_memory.init_db(DB_PATH)
        iris_memory.embed_and_store(db, note_id, "memory_note", content, OLLAMA_HOST,
                                    project_id=project_id)
        db.close()
    except Exception:
        pass


def _stream_ollama(enriched_prompt: str, model: str):
    """Synchronous generator — yields raw token strings from Ollama."""
    url = f"{OLLAMA_HOST.rstrip('/')}/api/generate"
    payload = {
        "model": model,
        "prompt": enriched_prompt,
        "stream": True,
        "keep_alive": "30m",
    }
    resp = requests.post(url, json=payload, timeout=300, stream=True)
    resp.raise_for_status()
    for line in resp.iter_lines():
        if line:
            try:
                data = json.loads(line.decode("utf-8"))
                token = data.get("response", "")
                if token:
                    yield token
            except ValueError:
                pass


def _chunk_markdown(content: str, max_chunk_tokens: int = 512) -> list[tuple[int, str, str]]:
    """Chunk markdown into (chunk_index, heading_path, chunk_text) tuples.

    Splits on headings first, then on double-newline within each section
    to keep chunks under max_chunk_tokens.
    """
    heading_pattern = re.compile(r"^(#{1,3})\s+(.+)", re.MULTILINE)
    sections: list[tuple[str, str]] = []  # (heading_path, text)
    heading_stack: list[tuple[int, str]] = []  # (level, text)
    last_pos = 0

    for match in heading_pattern.finditer(content):
        pre_text = content[last_pos:match.start()].strip()
        if pre_text:
            path = " > ".join(h[1] for h in heading_stack) if heading_stack else ""
            sections.append((path, pre_text))
        level = len(match.group(1))
        heading_text = match.group(2).strip()
        while heading_stack and heading_stack[-1][0] >= level:
            heading_stack.pop()
        heading_stack.append((level, heading_text))
        last_pos = match.end()

    remaining = content[last_pos:].strip()
    if remaining:
        path = " > ".join(h[1] for h in heading_stack) if heading_stack else ""
        sections.append((path, remaining))

    chunks: list[tuple[int, str, str]] = []
    idx = 0
    for heading_path, section_text in sections:
        paragraphs = [p.strip() for p in section_text.split("\n\n") if p.strip()]
        current: list[str] = []
        current_tokens = 0
        for para in paragraphs:
            para_tokens = len(para) // 4
            if current_tokens + para_tokens > max_chunk_tokens and current:
                chunks.append((idx, heading_path, "\n\n".join(current)))
                idx += 1
                current = [para]
                current_tokens = para_tokens
            else:
                current.append(para)
                current_tokens += para_tokens
        if current:
            chunks.append((idx, heading_path, "\n\n".join(current)))
            idx += 1

    return chunks


def _process_ingestion_job(db, job: dict) -> None:
    """Chunk and embed a single document. Called by the background worker."""
    job_id = job["id"]
    document_id = job["document_id"]
    project_id = job.get("project_id")
    try:
        row = db.execute(
            "SELECT raw_content, doc_type FROM documents WHERE id = ?", (document_id,)
        ).fetchone()
        if not row or not row["raw_content"]:
            iris_memory.update_ingestion_job(db, job_id, status="failed",
                                             error_message="No raw_content found")
            return

        chunks = _chunk_markdown(row["raw_content"])
        for chunk_index, heading_path, chunk_text in chunks:
            chunk_id = iris_memory.add_document_chunk(
                db, document_id, project_id, chunk_index, heading_path, chunk_text
            )
            db.commit()
            # Embed in a short-lived thread — fire and move on
            t = threading.Thread(
                target=_embed_chunk_in_background,
                args=(chunk_id, chunk_text, project_id),
                daemon=True,
            )
            t.start()

        iris_memory.update_document_chunk_count(db, document_id, len(chunks))
        iris_memory.update_ingestion_job(db, job_id, status="completed",
                                         chunk_count=len(chunks))
    except Exception as exc:
        iris_memory.update_ingestion_job(db, job_id, status="failed",
                                         error_message=str(exc))


def _embed_chunk_in_background(chunk_id: str, text: str, project_id: str = None) -> None:
    try:
        db = iris_memory.init_db(DB_PATH)
        iris_memory.embed_and_store(db, chunk_id, "document_chunk", text, OLLAMA_HOST,
                                    project_id=project_id)
        db.close()
    except Exception:
        pass


def _ingestion_worker() -> None:
    """Daemon thread: polls for queued ingestion jobs every 5 seconds."""
    while True:
        try:
            db = iris_memory.init_db(DB_PATH)
            job = iris_memory.claim_next_job(db)
            if job:
                _process_ingestion_job(db, job)
            db.close()
        except Exception:
            pass
        time.sleep(5)


# Start the ingestion worker at import time (daemon — exits with the process)
threading.Thread(target=_ingestion_worker, daemon=True, name="iris-ingestion-worker").start()


def _decompose_research_intent(intent: str, model: str) -> dict:
    """Convert a long operator intent into focused, Tavily-safe search queries.

    The full intent NEVER leaves IRIS — only the short decomposed queries are
    forwarded to Tavily.  Returns a dict:
        {
          "topic":          str,        # one-line description of the research area
          "queries":        list[str],  # 2-5 short queries, each ≤ MAX_TAVILY_QUERY_LEN
          "synthesis_goal": str,        # what the final synthesis should answer
        }
    Falls back gracefully on parse failure.
    """
    decomp_prompt = (
        "You are a research query compiler for a local AI system.\n"
        "The operator has expressed a research intent that may be long and multi-part.\n"
        "Your job: decompose it into 2-5 SHORT, focused web-search queries.\n"
        "Rules:\n"
        "  - Each query MUST be under 380 characters (ideally 40-150 chars)\n"
        "  - Each query should target one specific aspect or question\n"
        "  - Queries must be self-contained — no pronouns like 'it' or 'they'\n"
        "  - Do NOT include the operator's personal context or governance info\n"
        "  - Write queries as a researcher would type them into a search engine\n"
        "  - IMPORTANT: Do NOT silently substitute or correct entity names (game titles,\n"
        "    class names, build names, season numbers). If a name looks misspelled or\n"
        "    ambiguous, record it in entity_interpretations — do not guess silently.\n\n"
        f"Operator intent:\n{intent[:2000]}\n\n"
        "Return ONLY a JSON object with exactly these keys:\n"
        "  topic (string): one-line description of the research topic\n"
        "  queries (array of strings): 2-5 focused search queries\n"
        "  synthesis_goal (string): what should the final answer address\n"
        "  entity_interpretations (array): every entity name that was corrected, normalised,\n"
        "    or was ambiguous. Each item: {original: str, used_as: str, confidence: float 0-1}.\n"
        "    Use confidence < 0.8 for uncertain interpretations. Empty array if none.\n"
        "No explanation, no markdown fencing — raw JSON only."
    )
    tokens = list(_stream_ollama(decomp_prompt, model))
    raw = "".join(tokens).strip()
    raw = re.sub(r"^```(?:json)?\s*", "", raw)
    raw = re.sub(r"\s*```\s*$", "", raw.strip())
    try:
        result = json.loads(raw)
        queries = [
            q[:MAX_TAVILY_QUERY_LEN]
            for q in result.get("queries", [])
            if isinstance(q, str) and q.strip()
        ][:MAX_RESEARCH_SUBQUERIES]
        if not queries:
            raise ValueError("no queries parsed")
        raw_interps = result.get("entity_interpretations", [])
        entity_interpretations = [
            i for i in (raw_interps if isinstance(raw_interps, list) else [])
            if isinstance(i, dict) and i.get("original") and i.get("used_as")
        ][:10]
        return {
            "topic":                 str(result.get("topic", intent[:100])),
            "queries":               queries,
            "synthesis_goal":        str(result.get("synthesis_goal", intent))[:1200],
            "entity_interpretations": entity_interpretations,
        }
    except Exception as exc:
        logger.warning("[Research] Decomposition parse failed (%s) — single-query fallback", exc)
        return {
            "topic":                 intent[:100],
            "queries":               [intent[:MAX_TAVILY_QUERY_LEN]],
            "synthesis_goal":        intent,
            "entity_interpretations": [],
        }


def _execute_tavily_queries(
    queries: list[str],
    client,
) -> tuple[list[dict], list[str]]:
    """Run each decomposed query against Tavily; deduplicate results by URL.

    Returns:
        sources  — list of dicts: {subquery, url, title, snippet}
        answers  — list of Tavily inline answers (one per subquery that has one)
    """
    seen_urls: set[str] = set()
    sources: list[dict] = []
    answers: list[str]  = []

    for i, query in enumerate(queries):
        logger.info("[Research] Subquery %d/%d: %r", i + 1, len(queries), query[:100])
        try:
            resp = client.search(
                query=query,
                search_depth="basic",
                max_results=MAX_RESULTS_PER_SUBQUERY,
                include_answer=True,
                include_raw_content=False,
            )
        except Exception as exc:
            logger.warning("[Research]   subquery %d failed: %s", i + 1, exc)
            continue

        if resp.get("answer"):
            answers.append(f"[Subquery: {query[:80]}]\n{resp['answer']}")
            logger.info("[Research]   inline answer: %s", resp["answer"][:100])

        for item in resp.get("results", []):
            url = item.get("url", "").strip()
            if not url or url in seen_urls:
                logger.info("[Research]   skip (dup/empty url): %s", url[:80])
                continue
            seen_urls.add(url)
            snippet = item.get("content") or item.get("snippet") or item.get("raw_content") or ""
            sources.append({
                "subquery": query,
                "url":      url,
                "title":    item.get("title", "Untitled"),
                "snippet":  snippet[:600],
            })
            logger.info("[Research]   source: %s — %s", item.get("title", "?")[:50], url[:70])

    logger.info(
        "[Research] Execution complete. unique_sources=%d, answers=%d",
        len(sources), len(answers),
    )
    return sources, answers


def _synthesize_research(
    topic: str,
    synthesis_goal: str,
    answers: list[str],
    sources: list[dict],
    model: str,
) -> str:
    """Local LLM pass: synthesize collected answers and source snippets into a
    coherent structured response.  The operator intent is used to guide the
    synthesis goal — it never leaves the local node."""
    answers_block = "\n\n".join(answers) if answers else "(no inline answers retrieved)"
    # Cap sources fed into synthesis to avoid overflowing the local model context
    sources_block = "\n".join(
        f"- [{s['title']}]({s['url']}):\n  {s['snippet'][:400]}"
        for s in sources[:12]
    )
    synth_prompt = (
        f"You are IRIS, a research synthesis assistant operating within an epistemic authority framework.\n\n"
        f"Research topic: {topic}\n\n"
        f"Synthesis goal:\n{synthesis_goal[:800]}\n\n"
        f"Search engine answers:\n{answers_block[:2500]}\n\n"
        f"Source excerpts:\n{sources_block[:3000]}\n\n"
        "SYNTHESIS RULES:\n"
        "1. Treat sources according to their credibility signal:\n"
        "   - Published organizational frameworks, toolkits, and official guides: cite directly as authoritative.\n"
        "   - Operational / Informational docs: use as corroborating context; note provenance.\n"
        "   - Planning discussions, meeting notes, draft fragments: treat as background signal only.\n"
        "     Do NOT elevate individual quotes, anecdotal observations, or unresolved planning\n"
        "     discussion to organizational claims or stated positions.\n"
        "2. Corroboration rule: any claim supported only by planning discussion or anecdotal\n"
        "   fragments MUST be flagged: 'This appears in planning discussion only — not confirmed\n"
        "   in finalized organizational sources.'\n"
        "3. Uncertainty rule: if retrieved information is thin or conflicting, preserve that\n"
        "   uncertainty explicitly rather than filling gaps with inference.\n"
        "4. Version/patch/time-sensitive points must be flagged clearly.\n"
        "5. Be concise and structured. Cite sources by title where relevant.\n\n"
        "Write a clear synthesis covering:\n"
        "  1. Key findings directly relevant to the synthesis goal\n"
        "  2. Points that are version/patch/time-sensitive (flag them)\n"
        "  3. Claims that rest only on low-credibility sources (flag them)\n"
        "  4. Gaps or uncertainties in the retrieved information"
    )
    tokens = list(_stream_ollama(synth_prompt, model))
    return "".join(tokens).strip()


def _extract_research_candidates(raw_result: str, query: str, model: str) -> list[dict]:
    """Ask the model to extract 3-5 candidate memory notes from a research result."""
    extraction_prompt = (
        f"You completed research on: \"{query}\"\n\n"
        f"Research result:\n{raw_result[:3000]}\n\n"
        "Extract 3-5 facts worth adding to permanent memory.\n"
        "CRITICAL: Every fact MUST preserve its entity anchors — game title, character class or entity name, "
        "build name, and season — both inside the content text AND in the dedicated fields below.\n"
        "Do NOT write generic facts that strip out 'Warlock', 'Season 13', 'Dread Claws', etc.\n"
        "Each fact must be self-contained and unambiguous without any surrounding context.\n\n"
        "Return ONLY a JSON array. Each object must have exactly these keys:\n"
        "  content       — the full atomic fact; MUST name the game, class/entity, build, and season if known\n"
        "  scope         — 'research' for game/project-specific facts, 'global' for universal facts\n"
        "  tags          — comma-separated keywords or null\n"
        "  game          — game title exactly as found, e.g. 'Diablo IV', or null\n"
        "  entity_class  — character class or named entity, e.g. 'Warlock', 'Necromancer', or null\n"
        "  build_topic   — specific build or mechanic name, e.g. 'Dread Claws', 'Blood Lance', or null\n"
        "  season        — season label exactly as found, e.g. 'Season 13', or null\n"
        "  note_type     — one of: build_mechanic, stat, lore, patch_note, general\n"
        "  patch_sensitive — true if this fact may become outdated after a game patch, false otherwise\n\n"
        "No explanation, no markdown fencing — only the raw JSON array."
    )
    tokens = list(_stream_ollama(extraction_prompt, model))
    raw_json = "".join(tokens).strip()
    # Strip markdown code fences if the model added them
    raw_json = re.sub(r"^```(?:json)?\s*", "", raw_json)
    raw_json = re.sub(r"\s*```$", "", raw_json)
    try:
        candidates = json.loads(raw_json)
        if isinstance(candidates, list):
            return candidates[:5]
    except Exception:
        pass
    return []




def _build_context(db, session_id: str, prompt: str, project_id: str = None) -> tuple[str, dict]:
    """Assemble the 8-layer enriched prompt with per-layer token budgeting.

    Injection order (strict):
      1. [SYSTEM]            — identity file, no budget cap
      2. [OPERATOR NOTES]    — scope=operator
      3. [PROJECT: {name}]   — scope=project, matching project_id
      4. [GLOBAL MEMORY]     — scope=global, no project_id
      5. [RETRIEVED SOURCE MATERIAL]  — semantic search on document_chunks, authority-labeled
      6. [SESSION SUMMARY]   — rolling summary
      7. [RECENT CONVERSATION]
      8. User: {prompt}

    Returns (enriched_prompt_string, debug_dict).
    """
    budgets = {
        "operator":     int(MAX_CONTEXT_TOKENS * 0.10),
        "project":      int(MAX_CONTEXT_TOKENS * 0.25),
        "global":       int(MAX_CONTEXT_TOKENS * 0.10),
        "retrieval":    int(MAX_CONTEXT_TOKENS * 0.25),
        "summary":      int(MAX_CONTEXT_TOKENS * 0.10),
        "conversation": int(MAX_CONTEXT_TOKENS * 0.20),
    }
    debug: dict = {"budget": budgets, "max_context_tokens": MAX_CONTEXT_TOKENS}
    context_parts: list[str] = []

    # --- Pre-compute query embedding once — reused by Layer 3 (note relevance filter)
    # and Layer 5 (document chunk retrieval).  Graceful degradation on failure. ---
    query_vec: list[float] | None = None
    try:
        query_vec = iris_memory.get_embedding(prompt, OLLAMA_HOST)
    except Exception as _exc:
        logger.warning("[Context] Query embedding failed — semantic note filter disabled: %s", _exc)

    # --- Layer 1: System identity (always present, no cap) ---
    identity = _load_system_identity()
    if identity:
        context_parts.append(f"[SYSTEM]\n{identity}")
        debug["system"] = {"tokens": len(identity) // 4}

    # --- Layer 2: Operator notes ---
    op_notes = iris_memory.get_memory_notes(
        db, scope="operator", exclude_states=["archived", "deleted"]
    )
    if op_notes:
        raw = "\n".join(f"- {n['content']}" for n in op_notes)
        text, used = _apply_budget(raw, budgets["operator"])
        context_parts.append(f"[OPERATOR NOTES]\n{text}")
        debug["operator"] = {
            "count": len(op_notes),
            "notes": [{"id": n["id"], "scope": n["scope"], "project_id": n.get("project_id"), "preview": n["content"][:100]} for n in op_notes],
            "tokens": used,
        }
        for n in op_notes:
            iris_memory.touch_memory_note(db, n["id"])

    # --- Layer 3: Project notes (scope=project) + research findings (scope=research) ---
    if project_id:
        proj_notes = iris_memory.get_memory_notes(
            db, scope="project", project_id=project_id,
            exclude_states=["archived", "deleted"]
        )
        # BUG FIX: also include scope=research notes for this project.
        # Promoted research candidates use scope='research' — without this they are
        # stored correctly but never injected into context.
        research_notes = iris_memory.get_memory_notes(
            db, scope="research", project_id=project_id,
            exclude_states=["archived", "deleted"]
        )
        all_proj_notes = proj_notes + research_notes
        proj = iris_memory.get_project(db, project_id)
        proj_name = proj["name"] if proj else project_id
        logger.info(
            "[Context] Layer 3 — project=%r: %d project note(s), %d research note(s)",
            proj_name, len(proj_notes), len(research_notes),
        )
        # Relevance filter: select only notes pertinent to the current prompt so that
        # unrelated builds/characters in the same project are not blindly injected.
        # Primary:  semantic vector search on "memory_note" embeddings (post-promotion).
        # Fallback:  keyword match when embeddings are not yet available for older notes.
        total_proj_notes = len(all_proj_notes)
        filter_applied   = "none"
        if all_proj_notes and query_vec is not None:
            note_hits = iris_memory.search_memory(
                db, query_vec, top_k=8,
                source_type="memory_note",
                project_id=project_id,
            )
            if note_hits:
                threshold    = 0.30
                relevant_ids = {h["source_id"] for h in note_hits if h["score"] > threshold}
                if not relevant_ids:
                    relevant_ids = {note_hits[0]["source_id"]}  # always keep best match
                filtered = [n for n in all_proj_notes if n["id"] in relevant_ids]
                if filtered:
                    logger.info(
                        "[Context] Note semantic filter: %d → %d notes (threshold=%.2f, scores=%s)",
                        total_proj_notes, len(filtered), threshold,
                        [round(h["score"], 3) for h in note_hits[:4]],
                    )
                    all_proj_notes = filtered
                    filter_applied = "semantic"
        if filter_applied == "none" and all_proj_notes:
            # Keyword fallback: keep notes that share significant words with the prompt.
            prompt_tokens = {w.lower() for w in re.split(r"\W+", prompt) if len(w) >= 4}
            if prompt_tokens:
                kw_filtered = [
                    n for n in all_proj_notes
                    if any(tok in n["content"].lower() for tok in prompt_tokens)
                ]
                if kw_filtered:
                    logger.info(
                        "[Context] Note keyword filter: %d → %d notes (tokens: %s)",
                        total_proj_notes, len(kw_filtered), sorted(prompt_tokens)[:5],
                    )
                    all_proj_notes = kw_filtered
                    filter_applied = "keyword"
        if filter_applied == "none":
            logger.info(
                "[Context] No note filter applied; keeping all %d project notes", total_proj_notes
            )
        if all_proj_notes:
            # Prefix each note with its scope so the model can distinguish
            # project-level notes from research findings and, crucially,
            # notes about different characters/builds within the same project.
            raw = "\n".join(f"- [{n['scope']}] {n['content']}" for n in all_proj_notes)
            text, used = _apply_budget(raw, budgets["project"])
            context_parts.append(f"[PROJECT: {proj_name}]\n{text}")
            debug["project"] = {
                "name":           proj_name,
                "count":          total_proj_notes,
                "injected_count": len(all_proj_notes),
                "filter_applied": filter_applied,
                "notes": [
                    {"id": n["id"], "scope": n["scope"], "project_id": n.get("project_id"), "preview": n["content"][:100]}
                    for n in all_proj_notes
                ],
                "tokens": used,
            }
            for n in all_proj_notes:
                iris_memory.touch_memory_note(db, n["id"])

            # Grounding rules: injected immediately after project notes so the model
            # reads authority guidance before any retrieved source material or history.
            # Only present when project notes exist — no-op for global sessions.
            grounding = (
                f"[EPISTEMIC AUTHORITY RULES — project: {proj_name}]\n"
                f"\n"
                f"AUTHORITY HIERARCHY (highest → lowest):\n"
                f"  1. Operator correction in this message\n"
                f"  2. PROJECT MEMORY injected above — operator-approved, crystallized synthesis\n"
                f"     These are durable organizational knowledge. Treat them as established fact.\n"
                f"  3. RETRIEVED SOURCE MATERIAL (high-authority) — Definitive or Authoritative documents\n"
                f"     e.g. published frameworks, toolkits, mission statements, finalized guides\n"
                f"  4. RETRIEVED SOURCE MATERIAL (mid-authority) — Informational or operational docs\n"
                f"  5. RETRIEVED SOURCE MATERIAL (low-authority) — Contextual or Anecdotal sources\n"
                f"     e.g. strategic drafts, planning discussions, meeting notes\n"
                f"  6. Model prior knowledge — bounded contextual reasoning only (see below)\n"
                f"\n"
                f"DISTINCTION: PROJECT MEMORY vs. RETRIEVED SOURCE MATERIAL\n"
                f"- PROJECT MEMORY (Layer above) = crystallized, operator-reviewed organizational knowledge.\n"
                f"  Weight it like a trusted internal briefing document.\n"
                f"- RETRIEVED SOURCE MATERIAL (labeled below) = raw corpus with variable authority.\n"
                f"  Weight each chunk according to its [authority] label.\n"
                f"\n"
                f"SYNTHESIS RULES FOR SOURCE MATERIAL:\n"
                f"- High-authority sources (Definitive, Authoritative): synthesize directly; cite them.\n"
                f"- Mid-authority sources (Informational): use as supporting context; note provenance.\n"
                f"- Low-authority sources (Contextual, Anecdotal): treat as background signal only.\n"
                f"  Anecdotal observations, individual quotes, or unresolved planning discussion\n"
                f"  MUST NOT be elevated to organizational claims or used as primary evidence.\n"
                f"  If a low-authority source is your only support for a claim, flag it:\n"
                f"  \"This appears in planning discussion only — not confirmed in finalized documents.\"\n"
                f"- Corroboration rule: a claim supported by only one low-authority source requires\n"
                f"  explicit uncertainty labeling unless corroborated by PROJECT MEMORY or a\n"
                f"  higher-authority source.\n"
                f"\n"
                f"BOUNDED CONTEXTUAL REASONING (model prior):\n"
                f"- Where the organizational corpus establishes a political or analytical framework,\n"
                f"  you MAY reason from that framework even when a specific concept does not appear\n"
                f"  verbatim in the retrieved chunks.\n"
                f"- Example: if the corpus clearly reflects an abolitionist, intersectional, or\n"
                f"  decolonial framework, you may apply those lenses with explicit labeling:\n"
                f"  \"Consistent with CR's documented political framework, ...\"\n"
                f"  \"This aligns with the abolitionist orientation evident across the corpus ...\"\n"
                f"  \"An inference grounded in the organization's stated commitments: ...\"\n"
                f"- You MUST NOT fabricate positions the organization has not expressed anywhere in\n"
                f"  the corpus or PROJECT MEMORY. The framework is an inference aid, not a blank check.\n"
                f"- TODO (future): project-level concept registry will allow operators to explicitly\n"
                f"  pre-authorize specific frameworks for bounded inference.\n"
                f"\n"
                f"WHEN THE OPERATOR CORRECTS YOU:\n"
                f"- Acknowledge immediately. Check PROJECT MEMORY for supporting evidence.\n"
                f"- If notes confirm the correction, say so. If notes are silent, say:\n"
                f"  \"I don't see that in current project memory — I'll take your correction.\"\n"
                f"- NEVER argue, claim memory failure didn't occur, or say you cannot be corrected.\n"
                f"\n"
                f"HARD LIMITS:\n"
                f"- Do NOT fabricate specifics (statistics, quotes, names, dates) not in sources.\n"
                f"- Do NOT conflate sources from different documents or contexts.\n"
                f"- Do NOT use model prior to contradict explicit PROJECT MEMORY or Authoritative sources.\n"
                f"- If source detail is thin or only low-authority, preserve uncertainty explicitly.\n"
                f"- Do NOT flatten responses to verbatim repetition — analysis and synthesis are expected.\n"
                f"\n"
                f"Project memory and Authoritative sources are anchors. "
                f"Bounded inference extends the analysis — it does not replace the corpus."
            )
            context_parts.append(grounding)
            debug["grounding"] = {"injected": True, "project": proj_name}

            # Reduce recent-conversation budget when project notes are present so that
            # stale conversation about a different character cannot outweigh project memory.
            budgets["conversation"] = budgets["conversation"] // 2
            logger.info(
                "[Context] Grounding rules injected for project=%r; conversation budget halved to %d tokens",
                proj_name, budgets["conversation"],
            )
        else:
            debug["project"] = {"name": proj_name, "count": 0, "notes": []}

    # --- Layer 4: Global memory (scope=global, no project association) ---
    global_notes = iris_memory.get_memory_notes(
        db, scope="global", exclude_states=["archived", "deleted"]
    )
    # Only truly-global notes (no project association)
    global_notes = [n for n in global_notes if not n.get("project_id")]
    if global_notes:
        raw = "\n".join(f"- {n['content']}" for n in global_notes)
        text, used = _apply_budget(raw, budgets["global"])
        context_parts.append(f"[GLOBAL MEMORY]\n{text}")
        debug["global"] = {
            "count": len(global_notes),
            "notes": [{"id": n["id"], "scope": n["scope"], "project_id": n.get("project_id"), "preview": n["content"][:100]} for n in global_notes],
            "tokens": used,
        }
        for n in global_notes:
            iris_memory.touch_memory_note(db, n["id"])

    # --- Layer 5: Retrieved chunks (semantic search, project-scoped) ---
    # BUG FIX: always pass project_id so retrieval never crosses project boundaries.
    # When no project is active, project_id=None returns unfiltered results (correct
    # for global sessions). When a project is active, only that project's embedded
    # documents are searched — preventing cross-project context leakage.
    try:
        if query_vec is None:  # reuse pre-computed embedding from top of _build_context
            query_vec = iris_memory.get_embedding(prompt, OLLAMA_HOST)
        hits = iris_memory.search_memory(
            db, query_vec, top_k=5,
            source_type="document_chunk",
            project_id=project_id,          # ← governance boundary enforced here
        )
        logger.info(
            "[Context] Layer 5 — semantic retrieval: project_filter=%r, hits=%d",
            project_id, len(hits),
        )
        if hits:
            chunk_lines: list[str] = []
            retrieval_debug: list[dict] = []
            for hit in hits[:3]:
                chunk_row = db.execute(
                    "SELECT heading_path, document_id FROM document_chunks WHERE id = ?",
                    (hit["source_id"],),
                ).fetchone()
                doc_row = db.execute(
                    "SELECT filename, project_id, authority_level FROM documents WHERE id = ?",
                    (chunk_row["document_id"],),
                ).fetchone() if chunk_row else None
                if chunk_row and doc_row:
                    # Authority label: use stored value; fall back to filename heuristic
                    stored_auth = (doc_row["authority_level"] or "").strip()
                    if stored_auth and stored_auth != "Informational":
                        auth_label = stored_auth
                    else:
                        fn_lower = (doc_row["filename"] or "").lower()
                        import re as _re
                        if _re.search(r"framework|toolkit|identity|who.we.are|mission|principles|manifesto|constitution", fn_lower):
                            auth_label = "Authoritative"
                        elif _re.search(r"plan|draft|notes|meeting|discussion|debrief|summary", fn_lower):
                            auth_label = "Contextual"
                        else:
                            auth_label = stored_auth or "Informational"
                    label = f"[Source: {doc_row['filename']} > {chunk_row['heading_path'] or 'root'} | authority: {auth_label}]"
                    entry = {
                        "source": label,
                        "score": round(hit["score"], 3),
                        "authority": auth_label,
                        "doc_project_id": doc_row["project_id"],
                        "embedding_project_id": hit.get("project_id"),
                    }
                    logger.info("[Context]   chunk score=%.3f authority=%s doc_project=%r %s",
                                hit["score"], auth_label, doc_row["project_id"], label)
                    retrieval_debug.append(entry)
                    chunk_lines.append(f"{label}\n{hit['chunk_text']}")
            if chunk_lines:
                raw = "\n\n".join(chunk_lines)
                text, used = _apply_budget(raw, budgets["retrieval"])
                context_parts.append(f"[RETRIEVED SOURCE MATERIAL]\n{text}")
                debug["retrieval"] = {
                    "hits": retrieval_debug,
                    "tokens": used,
                    "project_filter": project_id,
                }
    except Exception as _exc:
        logger.warning("[Context] Layer 5 embedding/retrieval failed: %s", _exc)
        # embedding failures never block the response

    # --- Layers 6 + 7: Session summary and recent conversation ---
    summary_text, recent = iris_memory.get_context_with_budget(
        db, session_id, max_tokens=budgets["summary"] * 4, recent_n=8
    )
    if summary_text:
        context_parts.append(f"[SESSION SUMMARY]\n{summary_text}")
        debug["summary"] = {"tokens": len(summary_text) // 4}

    if recent:
        conv_lines = [
            f"{'User' if m['role'] == 'user' else 'IRIS'}: {m['content']}"
            for m in recent
        ]
        raw = "\n".join(conv_lines)
        text, used = _apply_budget(raw, budgets["conversation"])
        context_parts.append(
            f"[RECENT CONVERSATION — supplementary; if it conflicts with project notes "
            f"above, project notes take precedence]\n{text}"
        )
        debug["conversation"] = {"messages": len(recent), "tokens": used}

    # --- Layer 8: Current prompt ---
    context_parts.append(f"User: {prompt}")
    debug["prompt"] = {"tokens": len(prompt) // 4}

    return "\n\n".join(context_parts), debug



# ---------------------------------------------------------------------------
# Routes — Chat
# ---------------------------------------------------------------------------

@app.post("/chat")
async def chat(req: ChatRequest, background_tasks: BackgroundTasks):
    if req.mode == "research":
        return await _handle_research(req)

    session_id = req.session_id or str(uuid.uuid4())
    db = _get_db()
    iris_memory.create_session(db, session_id, model=req.model, mode=req.mode,
                               project_id=req.project_id)
    if req.project_id:
        iris_memory.update_project_last_used(db, req.project_id)

    enriched_prompt, context_debug = _build_context(
        db, session_id, req.prompt, project_id=req.project_id
    )
    _last_context_debug[session_id] = context_debug

    iris_memory.save_message(db, session_id, "user", req.prompt, model=req.model)
    # Close the request-thread connection now — save_results() runs in a different
    # thread (Starlette BackgroundTask) and must not reuse this connection.
    db.close()

    full_response: list[str] = []
    start_time = time.time()

    def generate():
        for token in _stream_ollama(enriched_prompt, req.model):
            full_response.append(token)
            yield token

    def save_results():
        """Background task: runs in a Starlette thread-pool thread.
        Opens its own SQLite connection — never reuses the request-thread connection."""
        response_text = "".join(full_response)
        duration_ms = int((time.time() - start_time) * 1000)
        db2 = iris_memory.init_db(DB_PATH)
        try:
            assistant_msg_id = iris_memory.save_message(
                db2, session_id, "assistant", response_text, model=req.model
            )
            iris_memory.save_model_run(db2, session_id, assistant_msg_id, req.model, duration_ms)
        finally:
            db2.close()
        embed_thread = threading.Thread(
            target=_embed_in_background,
            args=(assistant_msg_id, response_text, req.project_id),
            daemon=True,
        )
        embed_thread.start()
        embed_thread.join(timeout=8)

    background_tasks.add_task(save_results)
    return StreamingResponse(generate(), media_type="text/plain; charset=utf-8")


async def _handle_research(req: ChatRequest) -> StreamingResponse:
    """Research orchestration pipeline — streams progress markers to the client.

        Operator Intent
         → [1] Query Decomposition  (local LLM — intent stays on-node)
         → [2] Multi-Query Tavily   (only short, neutral subqueries leave IRIS)
         → [3] Source Deduplication
         → [4] Local Synthesis      (local LLM)
         → [5] Candidate Extraction (local LLM)
         → [6] Research Cache       (pending review in Memory Admin)
    """
    logger.info("[Research] Intent received: %r", req.prompt[:120])

    async def _generate():
        # ── 1. Import / key guards ─────────────────────────────────────────
        try:
            from tavily import TavilyClient
        except ImportError:
            yield (
                "[Research ERROR] tavily-python is not installed on the server.\n"
                "Fix: sudo /opt/iris/venv/bin/pip install tavily-python"
            )
            return
        api_key = os.getenv("TAVILY_API_KEY")
        if not api_key:
            yield (
                "[Research ERROR] TAVILY_API_KEY is not set in the server environment.\n"
                "Fix: add TAVILY_API_KEY=tvly-... to /opt/iris/.env and restart iris-api."
            )
            return
        client = TavilyClient(api_key=api_key)
        loop = asyncio.get_event_loop()

        # ── 2. Query decomposition ─────────────────────────────────────────
        yield "[⟳ Research] Decomposing intent into search queries…\n"
        logger.info("[Research] Decomposing intent into subqueries...")
        try:
            decomposition = await loop.run_in_executor(
                None, _decompose_research_intent, req.prompt, req.model
            )
            logger.info("[Research] Topic: %r", decomposition["topic"])
            for i, q in enumerate(decomposition["queries"]):
                logger.info("[Research] Subquery[%d]: %r (len=%d)", i, q[:100], len(q))
        except Exception as exc:
            logger.warning("[Research] Decomposition failed (%s) — single-query fallback", exc)
            decomposition = {
                "topic":                 req.prompt[:100],
                "queries":               [req.prompt[:MAX_TAVILY_QUERY_LEN]],
                "synthesis_goal":        req.prompt,
                "entity_interpretations": [],
            }

        # Surface any entity interpretation warnings in the stream
        for interp in decomposition.get("entity_interpretations", []):
            orig = (interp.get("original") or "").strip()
            used = (interp.get("used_as") or "").strip()
            if orig and used and orig.lower() != used.lower():
                conf = float(interp.get("confidence", 1.0))
                if conf < 0.8:
                    yield f"[⚠ Ambiguous] '{orig}' → '{used}' ({conf:.0%} confidence)\n"
                else:
                    yield f"[ℹ Interpreted] '{orig}' as '{used}'\n"

        # ── 3. Tavily execution ────────────────────────────────────────────
        n_queries = len(decomposition["queries"])
        yield f"[⟳ Research] Running {n_queries} web quer{'y' if n_queries == 1 else 'ies'} via Tavily…\n"
        try:
            sources, answers = await loop.run_in_executor(
                None, _execute_tavily_queries, decomposition["queries"], client
            )
        except Exception as exc:
            msg = f"[Research ERROR] Tavily execution failed: {exc}"
            logger.error(msg)
            yield msg
            return

        if not sources and not answers:
            yield (
                "[Research] No results returned from any subquery.\n"
                f"Subqueries attempted:\n"
                + "\n".join(f"  - {q}" for q in decomposition["queries"])
                + "\n\nCheck your TAVILY_API_KEY quota or try a simpler research intent."
            )
            return

        yield f"[⟳ Research] Synthesising {len(sources)} source(s)…\n"

        # ── 4. Local synthesis ─────────────────────────────────────────────
        logger.info("[Research] Synthesizing %d sources + %d answers...", len(sources), len(answers))
        try:
            synthesis = await loop.run_in_executor(
                None, _synthesize_research,
                decomposition["topic"], decomposition["synthesis_goal"],
                answers, sources, req.model
            )
            logger.info("[Research] Synthesis: %d chars", len(synthesis))
        except Exception as exc:
            logger.warning("[Research] Synthesis failed: %s", exc)
            synthesis = "\n\n".join(answers) if answers else "(synthesis unavailable — see sources below)"

        # ── 5. Candidate note extraction ───────────────────────────────────
        yield "[⟳ Research] Extracting structured memory candidates…\n"
        logger.info("[Research] Extracting candidate memory notes...")
        try:
            candidates = await loop.run_in_executor(
                None, _extract_research_candidates, synthesis, req.prompt, req.model
            )
            logger.info("[Research] Extracted %d candidate note(s)", len(candidates))
        except Exception as exc:
            logger.warning("[Research] Candidate extraction failed: %s", exc)
            candidates = []

        # ── 6. Build result text ───────────────────────────────────────────
        subquery_lines = "\n".join(
            f"  {i + 1}. {q}" for i, q in enumerate(decomposition["queries"])
        )
        source_lines = "\n".join(
            f"  [{s['title']}]({s['url']}) — via: \"{s['subquery'][:60]}\""
            for s in sources
        )
        raw_result = (
            f"Research Topic: {decomposition['topic']}\n\n"
            f"Subqueries generated ({len(decomposition['queries'])}):\n{subquery_lines}\n\n"
            f"Synthesis:\n{synthesis}\n\n"
            f"Sources ({len(sources)} unique):\n{source_lines}\n"
        )

        # ── 7. Save to research cache ──────────────────────────────────────
        session_id = req.session_id or str(uuid.uuid4())
        try:
            db = _get_db()
            iris_memory.create_session(db, session_id, model=req.model, mode="research",
                                       project_id=req.project_id)
            trace = {
                "topic":                  decomposition["topic"],
                "original_prompt":        req.prompt,
                "queries":                decomposition["queries"],
                "query_count":            len(decomposition["queries"]),
                "entity_interpretations": decomposition.get("entity_interpretations", []),
                "sources":                [
                    {"title": s["title"], "url": s["url"], "subquery": s["subquery"]}
                    for s in sources
                ],
                "source_count":     len(sources),
                "answers_count":    len(answers),
                "synthesis_length": len(synthesis),
                "candidate_count":  len(candidates),
                "model":            req.model,
            }
            iris_memory.save_research_cache(
                db, session_id, req.project_id or "", req.prompt, raw_result, candidates,
                trace=trace, model=req.model,
            )
            db.close()
            logger.info("[Research] Cache saved. session=%s candidates=%d", session_id, len(candidates))
        except Exception as exc:
            logger.error("[Research] Cache save failed: %s", exc)

        # ── 8. Yield final result to client ────────────────────────────────
        yield "\n"
        yield raw_result
        yield (
            f"---\n"
            f"Research complete. {len(candidates)} candidate note(s) queued for review.\n"
            f"Open Memory Admin → Research Review to promote notes to project memory."
        )
        logger.info("[Research] Pipeline complete.")

    return StreamingResponse(_generate(), media_type="text/plain; charset=utf-8")



# ---------------------------------------------------------------------------
# Routes — Memory
# ---------------------------------------------------------------------------

@app.get("/memory")
async def list_memory(
    scope: str | None = None,
    state: str | None = None,
    project_id: str | None = None,
):
    db = _get_db()
    notes = iris_memory.get_memory_notes(db, scope=scope, state=state, project_id=project_id)
    db.close()
    return [
        {
            "id":                n["id"],
            "scope":             n["scope"],
            "state":             n["state"],
            "project_id":        n.get("project_id"),
            "content":           n["content"],
            "tags":              n["tags"],
            "confidence":        n.get("confidence", 1.0),
            "usage_count":       n.get("usage_count", 0),
            "last_used_at":      n.get("last_used_at"),
            "source":            n.get("source"),
            "generated_by_model": n.get("generated_by_model"),
            "created_at":        n["created_at"],
        }
        for n in notes
    ]


@app.post("/memory/promote")
async def promote_memory(req: PromoteRequest):
    db = _get_db()
    note_id = iris_memory.add_memory_note(
        db,
        content=req.content,
        tags=req.tags,
        scope=req.scope,
        state=req.state,
        project_id=req.project_id,
        confidence=req.confidence,
        source=req.source,
        generated_by_model=req.generated_by_model,
    )
    db.close()
    return {"id": note_id, "scope": req.scope, "state": req.state}


@app.post("/memory/seed")
async def seed_memory(req: SeedRequest):
    db = _get_db()
    note_id = iris_memory.add_memory_note(
        db,
        content=req.content,
        tags=req.filename,
        scope="project",
        state="durable",
        project_id=req.project_id,
        source="document",
    )
    db.close()
    return {"id": note_id, "filename": req.filename}


@app.patch("/memory/{note_id}")
async def update_memory(note_id: str, req: NoteUpdateRequest):
    db = _get_db()
    iris_memory.update_memory_note(
        db, note_id,
        content=req.content,
        scope=req.scope,
        state=req.state,
        confidence=req.confidence,
        tags=req.tags,
    )
    db.close()
    return {"updated": note_id}


@app.post("/memory/{note_id}/archive")
async def archive_memory(note_id: str):
    db = _get_db()
    iris_memory.archive_memory_note(db, note_id)
    db.close()
    return {"archived": note_id}


@app.delete("/memory/{note_id}")
async def delete_memory(note_id: str):
    db = _get_db()
    iris_memory.delete_memory_note(db, note_id)
    db.close()
    return {"deleted": note_id}


# ---------------------------------------------------------------------------
# Routes — Projects
# ---------------------------------------------------------------------------

@app.get("/projects")
async def list_projects(status: str | None = None):
    db = _get_db()
    projects = iris_memory.get_projects(db, status=status)
    db.close()
    return projects


@app.post("/projects")
async def create_project(req: ProjectRequest):
    db = _get_db()
    project_id = iris_memory.create_project(db, req.name, req.description)
    db.close()
    return {"id": project_id, "name": req.name}


@app.get("/projects/{project_id}")
async def get_project(project_id: str):
    db = _get_db()
    proj = iris_memory.get_project(db, project_id)
    if not proj:
        db.close()
        raise HTTPException(status_code=404, detail="Project not found")
    note_count = db.execute(
        "SELECT COUNT(*) FROM memory_notes WHERE project_id = ? AND state NOT IN ('archived','deleted')",
        (project_id,),
    ).fetchone()[0]
    session_count = db.execute(
        "SELECT COUNT(*) FROM sessions WHERE project_id = ?", (project_id,)
    ).fetchone()[0]
    db.close()
    return {**proj, "note_count": note_count, "session_count": session_count}


@app.patch("/projects/{project_id}")
async def update_project(project_id: str, req: ProjectUpdateRequest):
    db = _get_db()
    proj = iris_memory.get_project(db, project_id)
    if not proj:
        db.close()
        raise HTTPException(status_code=404, detail="Project not found")
    iris_memory.update_project(db, project_id, name=req.name,
                               description=req.description, status=req.status)
    db.close()
    return {"updated": project_id}


@app.delete("/projects/{project_id}")
async def archive_project(project_id: str):
    """Soft-delete: sets status='archived'. Does not delete notes or sessions."""
    db = _get_db()
    iris_memory.update_project(db, project_id, status="archived")
    db.close()
    return {"archived": project_id}


@app.get("/projects/{project_id}/documents")
async def list_project_documents(project_id: str):
    db = _get_db()
    docs = iris_memory.get_documents(db, project_id=project_id)
    # Attach job status to each doc
    result = []
    for doc in docs:
        job = iris_memory.get_ingestion_job_for_document(db, doc["id"])
        result.append({**doc, "job_status": job["status"] if job else None})
    db.close()
    return result


# ---------------------------------------------------------------------------
# Routes — Documents
# ---------------------------------------------------------------------------

@app.post("/documents/ingest")
async def ingest_document(req: IngestRequest):
    """Accept a document, create DB rows, queue a background ingestion job.
    Returns immediately with job_id — never blocks on chunking or embedding."""
    file_hash = hashlib.sha256(req.content.encode()).hexdigest()
    db = _get_db()
    doc_id = iris_memory.add_document(
        db,
        project_id=req.project_id,
        filename=req.filename,
        file_hash=file_hash,
        doc_type=req.doc_type,
        raw_content=req.content,
        authority_level=req.authority_level,
        document_type=req.document_type,
        finality=req.finality,
    )
    job_id = iris_memory.create_ingestion_job(db, doc_id, project_id=req.project_id)
    db.close()
    return {"document_id": doc_id, "job_id": job_id, "status": "queued"}


# ---------------------------------------------------------------------------
# PDF extraction helpers
# ---------------------------------------------------------------------------

def _pdf_to_markdown(pdf_bytes: bytes, filename: str) -> str:
    """Extract text from a PDF using PyMuPDF and format it as page-separated markdown.

    Each page is wrapped with a `--- Page N ---` separator so downstream
    chunking can track page provenance.  Raises ValueError if the PDF appears
    to be image-only (< 50 characters of embedded text across the whole doc).
    """
    if not _PYMUPDF_AVAILABLE:
        raise RuntimeError("pymupdf is not installed on the server. Run: pip install pymupdf")

    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    parts: list[str] = []
    total_chars = 0

    for page_num in range(len(doc)):
        page = doc[page_num]
        raw = page.get_text("text")          # reliable embedded-text extraction

        # --- tidy up the raw text ---
        # Collapse runs of 3+ blank lines to 2 (common PDF artefact)
        text = re.sub(r"\n{3,}", "\n\n", raw).strip()

        # Attempt lightweight structure recovery:
        # Lines that are SHORT (≤ 80 chars) and title-cased / all-caps with no
        # trailing punctuation are promoted to H2-style headings.
        lines = []
        for ln in text.split("\n"):
            stripped = ln.strip()
            if (stripped and len(stripped) <= 80
                    and not stripped[-1] in ".,:;!?)"
                    and (stripped.isupper() or stripped.istitle())
                    and not any(c.isdigit() for c in stripped[:3])):
                lines.append(f"## {stripped}")
            else:
                lines.append(ln)
        text = "\n".join(lines)

        if text:
            total_chars += len(text)
            parts.append(f"--- Page {page_num + 1} ---\n\n{text}")

    doc.close()

    if total_chars < 50:
        raise ValueError(
            f"PDF '{filename}' appears to be scanned / image-only — "
            f"only {total_chars} characters of embedded text were found. "
            "OCR is not supported; please provide a text-layer PDF."
        )

    return "\n\n".join(parts)


def _save_pdf_sidecar(markdown: str, filename: str, file_hash: str) -> str:
    """Write the extracted markdown beside the DB for later inspection.
    Returns the sidecar path, or '' if the write failed."""
    try:
        os.makedirs(PDF_CACHE_DIR, exist_ok=True)
        stem = os.path.splitext(filename)[0]
        path = os.path.join(PDF_CACHE_DIR, f"{stem}_{file_hash[:8]}.md")
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(f"# {filename}\n\n")
            fh.write(markdown)
        return path
    except Exception as exc:
        logger.warning("Could not write PDF sidecar: %s", exc)
        return ""


@app.post("/documents/ingest-pdf")
async def ingest_pdf(
    file:            UploadFile = File(...),
    project_id:      str        = Form(...),
    authority_level: str        = Form("Informational"),
    document_type:   str        = Form("other"),
    finality:        str        = Form("final"),
):
    """Accept a PDF upload, extract text via PyMuPDF, normalise to markdown,
    and feed the result into the standard ingestion pipeline.

    Returns immediately with job_id — chunking / embedding run in the background.
    Raises 400 if the PDF has no embedded text (scanned / image-only).
    Raises 503 if pymupdf is not installed on the server.
    """
    if not _PYMUPDF_AVAILABLE:
        raise HTTPException(status_code=503,
                            detail="PDF ingestion unavailable: pymupdf not installed on server.")

    filename = file.filename or "upload.pdf"
    if not filename.lower().endswith(".pdf"):
        raise HTTPException(status_code=400, detail="Only PDF files are accepted at this endpoint.")

    pdf_bytes = await file.read()
    file_hash = hashlib.sha256(pdf_bytes).hexdigest()

    try:
        markdown = _pdf_to_markdown(pdf_bytes, filename)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))

    sidecar = _save_pdf_sidecar(markdown, filename, file_hash)

    db = _get_db()
    doc_id = iris_memory.add_document(
        db,
        project_id=project_id,
        filename=filename,
        file_hash=file_hash,
        doc_type="markdown",
        raw_content=markdown,
        authority_level=authority_level,
        document_type=document_type,
        finality=finality,
    )
    job_id = iris_memory.create_ingestion_job(db, doc_id, project_id=project_id)
    db.close()

    logger.info("PDF ingested: %s → doc %s (%d chars); sidecar: %s",
                filename, doc_id, len(markdown), sidecar or "none")

    return {
        "document_id": doc_id,
        "job_id":      job_id,
        "status":      "queued",
        "pages":       markdown.count("--- Page "),
        "chars":       len(markdown),
        "sidecar":     sidecar or None,
    }


@app.get("/documents/{doc_id}/status")
async def document_status(doc_id: str):
    db = _get_db()
    doc = db.execute(
        "SELECT id, filename, chunk_count FROM documents WHERE id = ?", (doc_id,)
    ).fetchone()
    if not doc:
        db.close()
        raise HTTPException(status_code=404, detail="Document not found")
    job = iris_memory.get_ingestion_job_for_document(db, doc_id)
    db.close()
    return {
        "document_id": doc_id,
        "filename": doc["filename"],
        "chunk_count": doc["chunk_count"],
        "job": dict(job) if job else None,
    }


@app.delete("/documents/{doc_id}")
async def delete_document(doc_id: str):
    db = _get_db()
    # Fetch filename + hash before deletion for sidecar cleanup
    doc_row = db.execute(
        "SELECT filename, file_hash FROM documents WHERE id = ?", (doc_id,)
    ).fetchone()
    iris_memory.delete_document(db, doc_id)
    db.close()

    # Clean up PDF markdown sidecar if one exists
    if doc_row and doc_row["file_hash"]:
        stem = os.path.splitext(doc_row["filename"] or "")[0]
        sidecar_path = os.path.join(
            PDF_CACHE_DIR, f"{stem}_{doc_row['file_hash'][:8]}.md"
        )
        try:
            if os.path.exists(sidecar_path):
                os.remove(sidecar_path)
                logger.info("Deleted PDF sidecar: %s", sidecar_path)
        except OSError as exc:
            logger.warning("Could not delete PDF sidecar %s: %s", sidecar_path, exc)

    return {"deleted": doc_id}


@app.patch("/documents/{doc_id}/metadata")
async def update_document_metadata(doc_id: str, req: DocumentMetadataUpdate):
    """Update epistemic authority metadata on an already-ingested document."""
    db = _get_db()
    doc = db.execute("SELECT id FROM documents WHERE id = ?", (doc_id,)).fetchone()
    if not doc:
        db.close()
        raise HTTPException(status_code=404, detail="Document not found")
    updates: list[str] = []
    params: list = []
    if req.authority_level is not None:
        updates.append("authority_level = ?"); params.append(req.authority_level)
    if req.document_type is not None:
        updates.append("document_type = ?");   params.append(req.document_type)
    if req.finality is not None:
        updates.append("finality = ?");        params.append(req.finality)
    if updates:
        params.append(doc_id)
        db.execute(f"UPDATE documents SET {', '.join(updates)} WHERE id = ?", params)
        db.commit()
    row = db.execute(
        "SELECT id, filename, authority_level, document_type, finality FROM documents WHERE id = ?",
        (doc_id,),
    ).fetchone()
    db.close()
    return dict(row)


@app.patch("/documents/{doc_id}/project")
async def move_document_project(doc_id: str, req: DocumentProjectUpdate):
    """Move a document and all its ingestion artifacts to a different project.
    Durable promoted memory notes are preserved in their current project."""
    db = _get_db()
    # Verify target project exists
    proj = db.execute("SELECT id FROM projects WHERE id = ?", (req.project_id,)).fetchone()
    if not proj:
        db.close()
        raise HTTPException(status_code=404, detail="Target project not found")
    moved = iris_memory.move_document_project(db, doc_id, req.project_id)
    db.close()
    if not moved:
        raise HTTPException(status_code=404, detail="Document not found")
    return {"moved": doc_id, "project_id": req.project_id}


# ---------------------------------------------------------------------------
# Routes — Research cache
# ---------------------------------------------------------------------------

@app.post("/research/preview")
async def research_preview(req: ChatRequest):
    """Fast decomposition preview — runs only the LLM decomposition step.

    Returns topic, generated queries, and entity interpretation warnings.
    No Tavily calls, no synthesis. Typically completes in 5-15 seconds.
    WinForms uses this to show a confirmation dialog before committing to a
    full research run.
    """
    loop = asyncio.get_event_loop()
    try:
        decomposition = await loop.run_in_executor(
            None, _decompose_research_intent, req.prompt, req.model
        )
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"Decomposition failed: {exc}")

    warnings: list[str] = []
    for interp in decomposition.get("entity_interpretations", []):
        orig = (interp.get("original") or "").strip()
        used = (interp.get("used_as") or "").strip()
        if not orig or not used:
            continue
        if orig.lower() == used.lower():
            continue
        conf = float(interp.get("confidence", 1.0))
        if conf < 0.8:
            warnings.append(
                f"⚠ Uncertain: '{orig}' → '{used}' ({conf:.0%} confidence) — "
                f"correct your intent if this is wrong"
            )
        else:
            warnings.append(f"Interpreted '{orig}' as '{used}'")

    logger.info(
        "[Research/preview] topic=%r queries=%d warnings=%d",
        decomposition["topic"], len(decomposition["queries"]), len(warnings),
    )
    return {
        "topic":                  decomposition["topic"],
        "queries":                decomposition["queries"],
        "synthesis_goal":         decomposition["synthesis_goal"],
        "entity_interpretations": decomposition.get("entity_interpretations", []),
        "warnings":               warnings,
    }


@app.get("/research/pending")
async def list_pending_research(project_id: str | None = None):
    db = _get_db()
    items = iris_memory.get_pending_research(db, project_id=project_id)
    db.close()
    return items


@app.get("/research/recent")
async def list_recent_research(limit: int = 50, project_id: str | None = None):
    """Return all research runs (any state) ordered newest-first, for the Trace tab.
    Does not include raw_result to keep the payload small."""
    db = _get_db()
    items = iris_memory.get_all_research(db, project_id=project_id, limit=limit)
    db.close()
    return items


@app.get("/research/{cache_id}/trace")
async def get_research_trace(cache_id: str):
    """Return the full research cache record (including raw_result) for trace detail view."""
    db = _get_db()
    record = iris_memory.get_research_by_id(db, cache_id)
    db.close()
    if not record:
        raise HTTPException(status_code=404, detail="Research run not found")
    return record


@app.post("/research/{cache_id}/promote")
async def promote_research(cache_id: str, req: ResearchPromoteRequest):
    """Promote operator-selected candidate notes to permanent memory."""
    db = _get_db()
    # Get the cache item to inherit project_id
    row = db.execute(
        "SELECT project_id, session_id FROM research_cache WHERE id = ?", (cache_id,)
    ).fetchone()
    if not row:
        db.close()
        raise HTTPException(status_code=404, detail="Research cache item not found")

    promoted_ids = []
    for note in req.selected_notes:
        # Build anchor prefix from structured entity fields so the identity
        # context is preserved inside the stored content itself.
        anchor_parts = [
            note.get("game") or "",
            note.get("entity_class") or "",
            note.get("build_topic") or "",
            note.get("season") or "",
        ]
        anchor_parts = [p for p in anchor_parts if p]
        base_content = note.get("content", "")
        if anchor_parts:
            full_content = f"[{' | '.join(anchor_parts)}]\n{base_content}"
        else:
            full_content = base_content

        note_id = iris_memory.add_memory_note(
            db,
            content=full_content,
            tags=note.get("tags"),
            scope=note.get("scope", "research"),
            state=note.get("state", "durable"),
            project_id=row["project_id"] or None,
            source="research",
            source_session_id=row["session_id"],
            game=note.get("game") or None,
            entity_class=note.get("entity_class") or None,
            build_topic=note.get("build_topic") or None,
            season=note.get("season") or None,
            note_type=note.get("note_type") or None,
            patch_sensitive=bool(note.get("patch_sensitive", False)),
        )
        promoted_ids.append(note_id)
        # Embed the promoted note so it is available for semantic note-relevance
        # filtering in Layer 3 of _build_context().  source_type="memory_note".
        threading.Thread(
            target=_embed_note_in_background,
            args=(note_id, note.get("content", ""), row["project_id"] or None),
            daemon=True,
        ).start()

    iris_memory.update_research_state(db, cache_id, "promoted")
    db.close()
    return {"promoted": promoted_ids, "cache_id": cache_id}


@app.delete("/research/{cache_id}")
async def discard_research(cache_id: str):
    db = _get_db()
    iris_memory.update_research_state(db, cache_id, "discarded")
    db.close()
    return {"discarded": cache_id}


# ---------------------------------------------------------------------------
# Routes — Sessions
# ---------------------------------------------------------------------------

@app.get("/sessions")
async def list_sessions():
    db = _get_db()
    rows = db.execute(
        "SELECT id, name, model, mode, created_at, project_id FROM sessions ORDER BY created_at DESC"
    ).fetchall()
    db.close()
    return [dict(r) for r in rows]


@app.post("/sessions/{session_id}/summarize")
async def summarize_session(session_id: str, model: str = DEFAULT_MODEL):
    db = _get_db()
    all_msgs = iris_memory.get_all_messages(db, session_id)
    if not all_msgs:
        db.close()
        raise HTTPException(status_code=404, detail="No messages found for that session")

    history = "\n".join(
        f"{'User' if m['role'] == 'user' else 'IRIS'}: {m['content']}"
        for m in all_msgs
    )
    summary_prompt = (
        "Summarize this conversation in 4-6 concise sentences, "
        "capturing the key topics and decisions:\n\n" + history
    )
    tokens = list(_stream_ollama(summary_prompt, model))
    summary = "".join(tokens)
    last_id = all_msgs[-1]["id"] if all_msgs else None
    iris_memory.save_session_summary(db, session_id, summary, last_id)
    db.close()
    return {"session_id": session_id, "summary": summary}


# ---------------------------------------------------------------------------
# Routes — Debug
# ---------------------------------------------------------------------------

@app.get("/debug/last-context")
async def debug_last_context(session_id: str):
    """Return the full 8-layer context debug info for the last request in a session."""
    debug = _last_context_debug.get(session_id)
    if not debug:
        raise HTTPException(status_code=404,
                            detail="No context debug found for this session (may have reset on restart)")
    return debug


@app.get("/debug/token-budget")
async def debug_token_budget():
    """Return the current context token budget allocation."""
    return {
        "max_context_tokens": MAX_CONTEXT_TOKENS,
        "allocation": {
            "system":       "no cap (reserved)",
            "operator":     f"10% = {int(MAX_CONTEXT_TOKENS * 0.10)} tokens",
            "project":      f"25% = {int(MAX_CONTEXT_TOKENS * 0.25)} tokens",
            "global":       f"10% = {int(MAX_CONTEXT_TOKENS * 0.10)} tokens",
            "retrieval":    f"25% = {int(MAX_CONTEXT_TOKENS * 0.25)} tokens",
            "summary":      f"10% = {int(MAX_CONTEXT_TOKENS * 0.10)} tokens",
            "conversation": f"20% = {int(MAX_CONTEXT_TOKENS * 0.20)} tokens",
        },
    }


# ---------------------------------------------------------------------------
# Routes — Health
# ---------------------------------------------------------------------------

@app.get("/health")
async def health():
    return {"status": "ok", "ollama": OLLAMA_HOST, "db": DB_PATH,
            "max_context_tokens": MAX_CONTEXT_TOKENS}


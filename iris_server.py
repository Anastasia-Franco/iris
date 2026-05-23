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

import hashlib
import json
import os
import re
import threading
import time
import uuid

import requests
from dotenv import load_dotenv
from fastapi import BackgroundTasks, FastAPI, HTTPException
from fastapi.responses import PlainTextResponse, StreamingResponse
from pydantic import BaseModel

import iris_memory

load_dotenv()

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

OLLAMA_HOST        = os.environ.get("IRIS_OLLAMA_HOST",         "http://127.0.0.1:11434")
DEFAULT_MODEL      = os.environ.get("IRIS_DEFAULT_MODEL",       "qwen3:30b")
DB_PATH            = os.environ.get("IRIS_DB_PATH",             "/opt/iris/data/iris_memory.db")
IDENTITY_FILE      = os.environ.get("IRIS_IDENTITY_FILE",       "/opt/iris/iris_identity.txt")
MAX_CONTEXT_TOKENS = int(os.environ.get("IRIS_MAX_CONTEXT_TOKENS", "6000"))

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
    project_id: str
    filename: str
    content: str
    doc_type: str = "markdown"

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


def _extract_research_candidates(raw_result: str, query: str, model: str) -> list[dict]:
    """Ask the model to extract 3-5 candidate memory notes from a research result."""
    extraction_prompt = (
        f"You completed research on: \"{query}\"\n\n"
        f"Research result:\n{raw_result[:3000]}\n\n"
        "Extract 3-5 concise facts from this research worth adding to permanent memory.\n"
        "Return ONLY a JSON array of objects with keys: content (string), scope (string: global or research), tags (string or null).\n"
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
      5. [RETRIEVED CHUNKS]  — semantic search on document_chunks
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
        debug["operator"] = {"count": len(op_notes), "tokens": used}
        for n in op_notes:
            iris_memory.touch_memory_note(db, n["id"])

    # --- Layer 3: Project notes ---
    if project_id:
        proj_notes = iris_memory.get_memory_notes(
            db, scope="project", project_id=project_id,
            exclude_states=["archived", "deleted"]
        )
        proj = iris_memory.get_project(db, project_id)
        proj_name = proj["name"] if proj else project_id
        if proj_notes:
            raw = "\n".join(f"- {n['content']}" for n in proj_notes)
            text, used = _apply_budget(raw, budgets["project"])
            context_parts.append(f"[PROJECT: {proj_name}]\n{text}")
            debug["project"] = {"name": proj_name, "count": len(proj_notes), "tokens": used}
            for n in proj_notes:
                iris_memory.touch_memory_note(db, n["id"])

    # --- Layer 4: Global memory ---
    global_notes = iris_memory.get_memory_notes(
        db, scope="global", exclude_states=["archived", "deleted"]
    )
    # Only truly-global notes (no project association)
    global_notes = [n for n in global_notes if not n.get("project_id")]
    if global_notes:
        raw = "\n".join(f"- {n['content']}" for n in global_notes)
        text, used = _apply_budget(raw, budgets["global"])
        context_parts.append(f"[GLOBAL MEMORY]\n{text}")
        debug["global"] = {"count": len(global_notes), "tokens": used}
        for n in global_notes:
            iris_memory.touch_memory_note(db, n["id"])

    # --- Layer 5: Retrieved chunks (semantic search) ---
    try:
        query_vec = iris_memory.get_embedding(prompt, OLLAMA_HOST)
        hits = iris_memory.search_memory(db, query_vec, top_k=3, source_type="document_chunk")
        if hits:
            chunk_lines: list[str] = []
            retrieval_debug: list[dict] = []
            for hit in hits:
                chunk_row = db.execute(
                    "SELECT heading_path, document_id FROM document_chunks WHERE id = ?",
                    (hit["source_id"],),
                ).fetchone()
                doc_row = db.execute(
                    "SELECT filename FROM documents WHERE id = ?",
                    (chunk_row["document_id"],),
                ).fetchone() if chunk_row else None
                if chunk_row and doc_row:
                    label = f"[Source: {doc_row['filename']} > {chunk_row['heading_path'] or 'root'}]"
                    retrieval_debug.append({"source": label, "score": round(hit["score"], 3)})
                    chunk_lines.append(f"{label}\n{hit['chunk_text']}")
            if chunk_lines:
                raw = "\n\n".join(chunk_lines)
                text, used = _apply_budget(raw, budgets["retrieval"])
                context_parts.append(f"[RETRIEVED CHUNKS]\n{text}")
                debug["retrieval"] = {"hits": retrieval_debug, "tokens": used}
    except Exception:
        pass  # embedding failures never block the response

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
        context_parts.append(f"[RECENT CONVERSATION]\n{text}")
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

    full_response: list[str] = []
    start_time = time.time()

    def generate():
        for token in _stream_ollama(enriched_prompt, req.model):
            full_response.append(token)
            yield token

    def save_results():
        response_text = "".join(full_response)
        duration_ms = int((time.time() - start_time) * 1000)
        assistant_msg_id = iris_memory.save_message(
            db, session_id, "assistant", response_text, model=req.model
        )
        iris_memory.save_model_run(db, session_id, assistant_msg_id, req.model, duration_ms)
        embed_thread = threading.Thread(
            target=_embed_in_background,
            args=(assistant_msg_id, response_text, req.project_id),
            daemon=True,
        )
        embed_thread.start()
        embed_thread.join(timeout=8)
        db.close()

    background_tasks.add_task(save_results)
    return StreamingResponse(generate(), media_type="text/plain; charset=utf-8")


async def _handle_research(req: ChatRequest) -> PlainTextResponse:
    """Research mode: query Tavily, extract candidate notes, save to review cache."""
    try:
        from tavily import TavilyClient
    except ImportError:
        raise HTTPException(status_code=500, detail="tavily-python not installed")

    api_key = os.getenv("TAVILY_API_KEY")
    if not api_key:
        raise HTTPException(status_code=500, detail="TAVILY_API_KEY not set")

    client = TavilyClient(api_key=api_key)
    results = client.search(
        query=req.prompt,
        search_depth="advanced",
        max_results=5,
        include_answer=True,
        include_raw_content=False,
    )

    raw_result = ""
    if results.get("answer"):
        raw_result += f"IRIS Research Answer:\n\n{results['answer']}\n\n"
    if results.get("results"):
        raw_result += "Sources:\n\n"
        for r in results["results"]:
            raw_result += f"- {r.get('title', 'Untitled')}\n  {r.get('url', '')}\n"

    # Extract candidate notes via second LLM call
    candidates = _extract_research_candidates(raw_result, req.prompt, req.model)

    # Save to research cache for operator review
    session_id = req.session_id or str(uuid.uuid4())
    db = _get_db()
    iris_memory.create_session(db, session_id, model=req.model, mode="research",
                               project_id=req.project_id)
    iris_memory.save_research_cache(
        db, session_id, req.project_id or "", req.prompt, raw_result, candidates
    )
    db.close()

    summary = (
        f"{raw_result}\n\n---\n"
        f"Research captured. {len(candidates)} candidate note(s) saved to review queue.\n"
        f"Use GET /research/pending to review and promote."
    )
    return PlainTextResponse(summary)



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
    )
    job_id = iris_memory.create_ingestion_job(db, doc_id, project_id=req.project_id)
    db.close()
    return {"document_id": doc_id, "job_id": job_id, "status": "queued"}


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
    iris_memory.delete_document(db, doc_id)
    db.close()
    return {"deleted": doc_id}


# ---------------------------------------------------------------------------
# Routes — Research cache
# ---------------------------------------------------------------------------

@app.get("/research/pending")
async def list_pending_research(project_id: str | None = None):
    db = _get_db()
    items = iris_memory.get_pending_research(db, project_id=project_id)
    db.close()
    return items


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
        note_id = iris_memory.add_memory_note(
            db,
            content=note.get("content", ""),
            tags=note.get("tags"),
            scope=note.get("scope", "research"),
            state=note.get("state", "durable"),
            project_id=row["project_id"] or None,
            source="research",
            source_session_id=row["session_id"],
        )
        promoted_ids.append(note_id)

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


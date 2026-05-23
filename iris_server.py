#!/usr/bin/env python3
"""
IRIS API Server — FastAPI service centralizing all IRIS intelligence on the Ubuntu node.

Run with:
    uvicorn iris_server:app --host 0.0.0.0 --port 8000

Environment variables (can also be set in .env):
    IRIS_OLLAMA_HOST   — default: http://127.0.0.1:11434
    IRIS_DEFAULT_MODEL — default: qwen3:30b
    IRIS_DB_PATH       — default: /opt/iris/data/iris_memory.db
"""

import json
import os
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

OLLAMA_HOST     = os.environ.get("IRIS_OLLAMA_HOST",    "http://127.0.0.1:11434")
DEFAULT_MODEL   = os.environ.get("IRIS_DEFAULT_MODEL",  "qwen3:30b")
DB_PATH         = os.environ.get("IRIS_DB_PATH",        "/opt/iris/data/iris_memory.db")
IDENTITY_FILE   = os.environ.get("IRIS_IDENTITY_FILE",  "/opt/iris/iris_identity.txt")

# ---------------------------------------------------------------------------
# App
# ---------------------------------------------------------------------------

app = FastAPI(title="IRIS API", version="1.0.0")

# ---------------------------------------------------------------------------
# Pydantic models
# ---------------------------------------------------------------------------

class ChatRequest(BaseModel):
    session_id: str | None = None
    prompt: str
    model: str = DEFAULT_MODEL
    mode: str = "prompt"          # "prompt" | "research"

class PromoteRequest(BaseModel):
    content: str
    scope: str = "durable"        # project | durable | pinned | operator

class SeedRequest(BaseModel):
    filename: str
    content: str

# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _load_system_identity() -> str:
    """Load the permanent system identity from a file (preferred), env var, or
    a built-in fallback.  The file is re-read on every request so edits take
    effect immediately without a server restart."""
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


def _embed_in_background(message_id: str, text: str) -> None:
    """Store an embedding for a saved message. Runs in a daemon thread."""
    try:
        db = iris_memory.init_db(DB_PATH)
        iris_memory.embed_and_store(db, message_id, "message", text, OLLAMA_HOST)
        db.close()
    except Exception:
        pass  # never surface embedding failures


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


def _build_context(db, session_id: str, prompt: str) -> str:
    """Assemble the multi-tier enriched prompt sent to Ollama."""
    context_parts = []

    # 1. Permanent identity — always first, loaded from iris_identity.txt
    identity = _load_system_identity()
    if identity:
        context_parts.append(f"[SYSTEM]\n{identity}")

    # 2. Operator notes from DB — runtime additions to system behaviour
    operator_notes = iris_memory.get_memory_notes(db, scope="operator")
    if operator_notes:
        sys_text = "\n".join(f"- {n['content']}" for n in operator_notes)
        context_parts.append(f"[OPERATOR NOTES]\n{sys_text}")

    project_notes = iris_memory.get_memory_notes(db, scope="project")
    if project_notes:
        notes_text = "\n".join(f"- {n['content'][:400]}" for n in project_notes)
        context_parts.append(f"[PROJECT MEMORY]\n{notes_text}")

    pinned_notes = (
        iris_memory.get_memory_notes(db, scope="pinned") +
        iris_memory.get_memory_notes(db, scope="durable")
    )
    if pinned_notes:
        facts_text = "\n".join(f"- {n['content'][:400]}" for n in pinned_notes)
        context_parts.append(f"[PINNED FACTS]\n{facts_text}")

    summary_text, recent = iris_memory.get_context_with_budget(
        db, session_id, max_tokens=3000, recent_n=8
    )
    if summary_text:
        context_parts.append(f"[SESSION SUMMARY]\n{summary_text}")

    if recent:
        history_lines = [
            f"{'User' if m['role'] == 'user' else 'IRIS'}: {m['content']}"
            for m in recent
        ]
        context_parts.append("[RECENT CONVERSATION]\n" + "\n".join(history_lines))

    context_parts.append(f"User: {prompt}")
    return "\n\n".join(context_parts)

# ---------------------------------------------------------------------------
# Routes — Chat
# ---------------------------------------------------------------------------

@app.post("/chat")
async def chat(req: ChatRequest, background_tasks: BackgroundTasks):
    if req.mode == "research":
        return await _handle_research(req)

    session_id = req.session_id or str(uuid.uuid4())
    db = _get_db()
    iris_memory.create_session(db, session_id, model=req.model, mode=req.mode)

    enriched_prompt = _build_context(db, session_id, req.prompt)
    iris_memory.save_message(db, session_id, "user", req.prompt, model=req.model)

    full_response: list[str] = []
    start_time = time.time()

    def generate():
        for token in _stream_ollama(enriched_prompt, req.model):
            full_response.append(token)
            yield token

    def save_results():
        """Runs after the streaming response is fully sent."""
        response_text = "".join(full_response)
        duration_ms = int((time.time() - start_time) * 1000)
        assistant_msg_id = iris_memory.save_message(
            db, session_id, "assistant", response_text, model=req.model
        )
        iris_memory.save_model_run(db, session_id, assistant_msg_id, req.model, duration_ms)
        embed_thread = threading.Thread(
            target=_embed_in_background,
            args=(assistant_msg_id, response_text),
            daemon=True,
        )
        embed_thread.start()
        embed_thread.join(timeout=8)
        db.close()

    background_tasks.add_task(save_results)
    return StreamingResponse(generate(), media_type="text/plain; charset=utf-8")


async def _handle_research(req: ChatRequest) -> PlainTextResponse:
    try:
        from tavily import TavilyClient
    except ImportError:
        raise HTTPException(status_code=500, detail="tavily-python not installed on this server")

    api_key = os.getenv("TAVILY_API_KEY")
    if not api_key:
        raise HTTPException(status_code=500, detail="TAVILY_API_KEY not set in server environment")

    client = TavilyClient(api_key=api_key)
    results = client.search(
        query=req.prompt,
        search_depth="advanced",
        max_results=5,
        include_answer=True,
        include_raw_content=False,
    )

    text = ""
    if results.get("answer"):
        text += f"IRIS Research Answer:\n\n{results['answer']}\n\n"
    if results.get("results"):
        text += "Sources:\n\n"
        for r in results["results"]:
            text += f"- {r.get('title', 'Untitled')}\n  {r.get('url', '')}\n"

    return PlainTextResponse(text)

# ---------------------------------------------------------------------------
# Routes — Memory
# ---------------------------------------------------------------------------

@app.get("/memory")
async def list_memory(scope: str | None = None):
    db = _get_db()
    notes = iris_memory.get_memory_notes(db, scope=scope)
    db.close()
    return [
        {
            "id":         n["id"],
            "scope":      n["scope"],
            "content":    n["content"],
            "tags":       n["tags"],
            "created_at": n["created_at"],
        }
        for n in notes
    ]


@app.post("/memory/promote")
async def promote_memory(req: PromoteRequest):
    db = _get_db()
    note_id = iris_memory.add_memory_note(db, req.content, scope=req.scope)
    db.close()
    return {"id": note_id, "scope": req.scope}


@app.post("/memory/seed")
async def seed_memory(req: SeedRequest):
    db = _get_db()
    note_id = iris_memory.add_memory_note(db, req.content, tags=req.filename, scope="project")
    db.close()
    return {"id": note_id, "filename": req.filename}


@app.delete("/memory/{note_id}")
async def delete_memory(note_id: str):
    db = _get_db()
    iris_memory.delete_memory_note(db, note_id)
    db.close()
    return {"deleted": note_id}

# ---------------------------------------------------------------------------
# Routes — Sessions
# ---------------------------------------------------------------------------

@app.get("/sessions")
async def list_sessions():
    db = _get_db()
    rows = db.execute(
        "SELECT id, name, model, mode, created_at FROM sessions ORDER BY created_at DESC"
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
# Routes — Health
# ---------------------------------------------------------------------------

@app.get("/health")
async def health():
    return {"status": "ok", "ollama": OLLAMA_HOST, "db": DB_PATH}

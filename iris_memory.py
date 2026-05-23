#!/usr/bin/env python3
"""IRIS Memory Module — SQLite-backed session, message, and memory storage."""

import sqlite3
import os
import json
import math
import uuid
from datetime import datetime, timezone

DEFAULT_DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "iris_memory.db")


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


# ---------------------------------------------------------------------------
# Database initialisation
# ---------------------------------------------------------------------------

def init_db(db_path: str = DEFAULT_DB_PATH) -> sqlite3.Connection:
    """Create all tables if they don't exist and return an open connection."""
    os.makedirs(os.path.dirname(db_path), exist_ok=True)
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS sessions (
            id          TEXT PRIMARY KEY,
            name        TEXT,
            model       TEXT,
            mode        TEXT,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS messages (
            id          TEXT PRIMARY KEY,
            session_id  TEXT NOT NULL REFERENCES sessions(id),
            role        TEXT NOT NULL CHECK(role IN ('user', 'assistant')),
            content     TEXT NOT NULL,
            model       TEXT,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS files (
            id          TEXT PRIMARY KEY,
            session_id  TEXT NOT NULL REFERENCES sessions(id),
            path        TEXT NOT NULL,
            content     TEXT,
            ingested_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS model_runs (
            id          TEXT PRIMARY KEY,
            session_id  TEXT NOT NULL REFERENCES sessions(id),
            message_id  TEXT REFERENCES messages(id),
            model       TEXT NOT NULL,
            duration_ms INTEGER,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS memory_notes (
            id          TEXT PRIMARY KEY,
            content     TEXT NOT NULL,
            tags        TEXT,
            scope       TEXT NOT NULL DEFAULT 'durable',
            created_at  TEXT NOT NULL,
            promoted_at TEXT
        );

        CREATE TABLE IF NOT EXISTS embeddings (
            id          TEXT PRIMARY KEY,
            source_id   TEXT NOT NULL,
            source_type TEXT NOT NULL,
            chunk_text  TEXT NOT NULL,
            vector      TEXT,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS session_summaries (
            id                      TEXT PRIMARY KEY,
            session_id              TEXT NOT NULL REFERENCES sessions(id),
            summary                 TEXT NOT NULL,
            covers_up_to_message_id TEXT,
            created_at              TEXT NOT NULL
        );
    """)
    conn.commit()
    return conn


# ---------------------------------------------------------------------------
# Sessions
# ---------------------------------------------------------------------------

def create_session(
    conn: sqlite3.Connection,
    session_id: str,
    model: str = None,
    mode: str = None,
    name: str = None,
) -> str:
    """Insert a new session row (no-op if the session already exists)."""
    conn.execute(
        "INSERT OR IGNORE INTO sessions (id, name, model, mode, created_at) VALUES (?, ?, ?, ?, ?)",
        (session_id, name, model, mode, _now()),
    )
    conn.commit()
    return session_id


# ---------------------------------------------------------------------------
# Messages
# ---------------------------------------------------------------------------

def save_message(
    conn: sqlite3.Connection,
    session_id: str,
    role: str,
    content: str,
    model: str = None,
) -> str:
    """Persist a single user or assistant message and return its ID."""
    message_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO messages (id, session_id, role, content, model, created_at) VALUES (?, ?, ?, ?, ?, ?)",
        (message_id, session_id, role, content, model, _now()),
    )
    conn.commit()
    return message_id


def get_recent_messages(
    conn: sqlite3.Connection,
    session_id: str,
    n: int = 10,
) -> list[dict]:
    """Return the last *n* messages for a session, oldest first."""
    cursor = conn.execute(
        """
        SELECT role, content FROM (
            SELECT role, content, created_at
            FROM messages
            WHERE session_id = ?
            ORDER BY created_at DESC
            LIMIT ?
        ) ORDER BY created_at ASC
        """,
        (session_id, n),
    )
    return [dict(row) for row in cursor.fetchall()]


# ---------------------------------------------------------------------------
# Files
# ---------------------------------------------------------------------------

def save_file_reference(
    conn: sqlite3.Connection,
    session_id: str,
    path: str,
    content: str,
) -> str:
    """Record a file that was ingested in this session."""
    file_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO files (id, session_id, path, content, ingested_at) VALUES (?, ?, ?, ?, ?)",
        (file_id, session_id, path, content, _now()),
    )
    conn.commit()
    return file_id


# ---------------------------------------------------------------------------
# Model runs
# ---------------------------------------------------------------------------

def save_model_run(
    conn: sqlite3.Connection,
    session_id: str,
    message_id: str,
    model: str,
    duration_ms: int,
) -> str:
    """Record a model invocation with its duration."""
    run_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO model_runs (id, session_id, message_id, model, duration_ms, created_at) VALUES (?, ?, ?, ?, ?, ?)",
        (run_id, session_id, message_id, model, duration_ms, _now()),
    )
    conn.commit()
    return run_id


# ---------------------------------------------------------------------------
# Memory notes
# ---------------------------------------------------------------------------

def add_memory_note(
    conn: sqlite3.Connection,
    content: str,
    tags: str = None,
    scope: str = "durable",
) -> str:
    """Manually promote a fact into long-term memory."""
    note_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO memory_notes (id, content, tags, scope, created_at, promoted_at) VALUES (?, ?, ?, ?, ?, ?)",
        (note_id, content, tags, scope, _now(), _now()),
    )
    conn.commit()
    return note_id


def get_memory_notes(
    conn: sqlite3.Connection,
    scope: str = None,
) -> list[dict]:
    """Return all memory notes, optionally filtered by scope."""
    if scope:
        cursor = conn.execute(
            "SELECT id, content, tags, scope, created_at FROM memory_notes WHERE scope = ? ORDER BY created_at ASC",
            (scope,),
        )
    else:
        cursor = conn.execute(
            "SELECT id, content, tags, scope, created_at FROM memory_notes ORDER BY created_at ASC"
        )
    return [dict(row) for row in cursor.fetchall()]


def delete_memory_note(conn: sqlite3.Connection, note_id: str) -> None:
    """Permanently remove a memory note by ID."""
    conn.execute("DELETE FROM memory_notes WHERE id = ?", (note_id,))
    conn.commit()


# ---------------------------------------------------------------------------
# Embeddings
# ---------------------------------------------------------------------------

def _cosine_similarity(a: list[float], b: list[float]) -> float:
    dot = sum(x * y for x, y in zip(a, b))
    mag_a = math.sqrt(sum(x * x for x in a))
    mag_b = math.sqrt(sum(x * x for x in b))
    if mag_a == 0 or mag_b == 0:
        return 0.0
    return dot / (mag_a * mag_b)


def get_embedding(text: str, host: str, model: str = "nomic-embed-text") -> list[float]:
    """Call Ollama /api/embeddings and return the vector as a Python list."""
    import requests
    url = f"{host.rstrip('/')}/api/embeddings"
    resp = requests.post(url, json={"model": model, "prompt": text}, timeout=60)
    resp.raise_for_status()
    return resp.json()["embedding"]


def embed_and_store(
    conn: sqlite3.Connection,
    source_id: str,
    source_type: str,
    chunk_text: str,
    host: str,
) -> str:
    """Embed a chunk of text and persist the vector to the database."""
    vector = get_embedding(chunk_text, host)
    embedding_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO embeddings (id, source_id, source_type, chunk_text, vector, created_at) VALUES (?, ?, ?, ?, ?, ?)",
        (embedding_id, source_id, source_type, chunk_text, json.dumps(vector), _now()),
    )
    conn.commit()
    return embedding_id


def search_memory(
    conn: sqlite3.Connection,
    query_vector: list[float],
    top_k: int = 3,
    source_type: str = None,
) -> list[dict]:
    """Return the top-k most similar chunks by cosine similarity."""
    if source_type:
        cursor = conn.execute(
            "SELECT id, source_id, source_type, chunk_text, vector FROM embeddings WHERE source_type = ?",
            (source_type,),
        )
    else:
        cursor = conn.execute(
            "SELECT id, source_id, source_type, chunk_text, vector FROM embeddings"
        )

    rows = cursor.fetchall()
    scored = []
    for row in rows:
        if row["vector"]:
            vec = json.loads(row["vector"])
            score = _cosine_similarity(query_vector, vec)
            scored.append({"score": score, **dict(row)})

    scored.sort(key=lambda x: x["score"], reverse=True)
    return scored[:top_k]


# ---------------------------------------------------------------------------
# Session summaries
# ---------------------------------------------------------------------------

def get_all_messages(conn: sqlite3.Connection, session_id: str) -> list[dict]:
    """Return all messages for a session, oldest first."""
    cursor = conn.execute(
        "SELECT id, role, content, created_at FROM messages WHERE session_id = ? ORDER BY created_at ASC",
        (session_id,),
    )
    return [dict(row) for row in cursor.fetchall()]


def save_session_summary(
    conn: sqlite3.Connection,
    session_id: str,
    summary: str,
    covers_up_to_message_id: str = None,
) -> str:
    """Persist a rolling summary for a session."""
    summary_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO session_summaries (id, session_id, summary, covers_up_to_message_id, created_at) VALUES (?, ?, ?, ?, ?)",
        (summary_id, session_id, summary, covers_up_to_message_id, _now()),
    )
    conn.commit()
    return summary_id


def get_session_summary(conn: sqlite3.Connection, session_id: str) -> dict | None:
    """Return the most recent summary for a session, or None."""
    cursor = conn.execute(
        "SELECT id, summary, covers_up_to_message_id FROM session_summaries WHERE session_id = ? ORDER BY created_at DESC LIMIT 1",
        (session_id,),
    )
    row = cursor.fetchone()
    return dict(row) if row else None


def get_context_with_budget(
    conn: sqlite3.Connection,
    session_id: str,
    max_tokens: int = 3000,
    recent_n: int = 8,
) -> tuple[str | None, list[dict]]:
    """Return (summary_text | None, recent_messages) respecting a token budget.

    Keeps the last `recent_n` messages verbatim. If total estimated tokens
    exceed `max_tokens`, oldest verbatim messages are trimmed first.
    A stored summary (if any) is returned alongside to cover older context.
    """
    recent = get_recent_messages(conn, session_id, n=recent_n)

    # Trim oldest verbatim messages if over budget (1 token ≈ 4 chars)
    while recent and sum(len(m["content"]) // 4 for m in recent) > max_tokens:
        recent = recent[1:]

    summary_row = get_session_summary(conn, session_id)
    summary_text = summary_row["summary"] if summary_row else None

    return summary_text, recent

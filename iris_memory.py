#!/usr/bin/env python3
"""IRIS Memory Module — SQLite-backed session, message, and memory storage.

Memory notes use two independent axes:
  scope  — governance boundary: operator | project | global | research | session
  state  — lifecycle tier:      ephemeral | session | durable | pinned | archived | deleted
"""

import sqlite3
import os
import json
import math
import uuid
from datetime import datetime, timezone

DEFAULT_DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "iris_memory.db")

_DEFAULT_PROJECTS = [
    ("IRIS", "Core system development and infrastructure"),
]


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


# ---------------------------------------------------------------------------
# Database initialisation
# ---------------------------------------------------------------------------

def init_db(db_path: str = DEFAULT_DB_PATH) -> sqlite3.Connection:
    """Create all tables, run schema migrations, seed defaults, return connection."""
    os.makedirs(os.path.dirname(db_path), exist_ok=True)
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS projects (
            id           TEXT PRIMARY KEY,
            name         TEXT NOT NULL,
            description  TEXT,
            status       TEXT NOT NULL DEFAULT 'active',
            created_at   TEXT NOT NULL,
            last_used_at TEXT
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id          TEXT PRIMARY KEY,
            name        TEXT,
            model       TEXT,
            mode        TEXT,
            created_at  TEXT NOT NULL,
            project_id  TEXT REFERENCES projects(id)
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
            ingested_at TEXT NOT NULL,
            project_id  TEXT REFERENCES projects(id)
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
            id                  TEXT PRIMARY KEY,
            content             TEXT NOT NULL,
            tags                TEXT,
            scope               TEXT NOT NULL DEFAULT 'global',
            state               TEXT NOT NULL DEFAULT 'durable',
            project_id          TEXT REFERENCES projects(id),
            created_at          TEXT NOT NULL,
            promoted_at         TEXT,
            expires_at          TEXT,
            confidence          REAL NOT NULL DEFAULT 1.0,
            last_used_at        TEXT,
            usage_count         INTEGER NOT NULL DEFAULT 0,
            source              TEXT,
            origin_note_id      TEXT REFERENCES memory_notes(id),
            source_document_id  TEXT,
            source_chunk_id     TEXT,
            source_session_id   TEXT REFERENCES sessions(id),
            generated_by_model  TEXT
        );

        CREATE TABLE IF NOT EXISTS embeddings (
            id          TEXT PRIMARY KEY,
            source_id   TEXT NOT NULL,
            source_type TEXT NOT NULL,
            chunk_text  TEXT NOT NULL,
            vector      TEXT,
            project_id  TEXT REFERENCES projects(id),
            created_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS session_summaries (
            id                      TEXT PRIMARY KEY,
            session_id              TEXT NOT NULL REFERENCES sessions(id),
            summary                 TEXT NOT NULL,
            covers_up_to_message_id TEXT,
            created_at              TEXT NOT NULL,
            project_id              TEXT REFERENCES projects(id)
        );

        CREATE TABLE IF NOT EXISTS documents (
            id          TEXT PRIMARY KEY,
            project_id  TEXT REFERENCES projects(id),
            filename    TEXT NOT NULL,
            file_hash   TEXT,
            file_path   TEXT,
            doc_type    TEXT NOT NULL DEFAULT 'markdown',
            ingested_at TEXT NOT NULL,
            chunk_count INTEGER NOT NULL DEFAULT 0,
            raw_content TEXT
        );

        CREATE TABLE IF NOT EXISTS document_chunks (
            id           TEXT PRIMARY KEY,
            document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            project_id   TEXT REFERENCES projects(id),
            chunk_index  INTEGER NOT NULL,
            heading_path TEXT,
            chunk_text   TEXT NOT NULL,
            created_at   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ingestion_jobs (
            id            TEXT PRIMARY KEY,
            document_id   TEXT NOT NULL REFERENCES documents(id),
            project_id    TEXT REFERENCES projects(id),
            status        TEXT NOT NULL DEFAULT 'queued',
            chunk_count   INTEGER NOT NULL DEFAULT 0,
            started_at    TEXT,
            completed_at  TEXT,
            error_message TEXT,
            created_at    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS research_cache (
            id              TEXT PRIMARY KEY,
            session_id      TEXT REFERENCES sessions(id),
            project_id      TEXT REFERENCES projects(id),
            query           TEXT NOT NULL,
            raw_result      TEXT,
            candidate_notes TEXT,
            state           TEXT NOT NULL DEFAULT 'pending',
            created_at      TEXT NOT NULL,
            reviewed_at     TEXT
        );
    """)
    conn.commit()
    _migrate_schema(conn)
    _migrate_scope_values(conn)
    _seed_default_projects(conn)
    return conn


def _migrate_schema(conn: sqlite3.Connection) -> None:
    """Add new columns to existing tables if they don't exist yet (idempotent)."""
    migrations = [
        ("sessions",          "project_id TEXT REFERENCES projects(id)"),
        ("files",             "project_id TEXT REFERENCES projects(id)"),
        ("embeddings",        "project_id TEXT REFERENCES projects(id)"),
        ("session_summaries", "project_id TEXT REFERENCES projects(id)"),
        ("memory_notes",      "state TEXT NOT NULL DEFAULT 'durable'"),
        ("memory_notes",      "project_id TEXT REFERENCES projects(id)"),
        ("memory_notes",      "expires_at TEXT"),
        ("memory_notes",      "confidence REAL NOT NULL DEFAULT 1.0"),
        ("memory_notes",      "last_used_at TEXT"),
        ("memory_notes",      "usage_count INTEGER NOT NULL DEFAULT 0"),
        ("memory_notes",      "source TEXT"),
        ("memory_notes",      "origin_note_id TEXT"),
        ("memory_notes",      "source_document_id TEXT"),
        ("memory_notes",      "source_chunk_id TEXT"),
        ("memory_notes",      "source_session_id TEXT"),
        ("memory_notes",      "generated_by_model TEXT"),
        ("documents",          "raw_content TEXT"),
        # Entity anchor columns — preserve identity context extracted during research
        ("memory_notes",      "game TEXT"),
        ("memory_notes",      "entity_class TEXT"),
        ("memory_notes",      "build_topic TEXT"),
        ("memory_notes",      "season TEXT"),
        ("memory_notes",      "note_type TEXT"),
        ("memory_notes",      "patch_sensitive INTEGER NOT NULL DEFAULT 0"),
        # Research trace — structured metadata for the Research Trace tab
        ("research_cache",    "trace_json TEXT"),
        ("research_cache",    "model TEXT"),
        # Epistemic authority — document-level synthesis weighting
        ("documents",         "authority_level TEXT NOT NULL DEFAULT 'Informational'"),
        ("documents",         "document_type TEXT NOT NULL DEFAULT 'other'"),
        ("documents",         "finality TEXT NOT NULL DEFAULT 'final'"),
    ]
    for table, col_def in migrations:
        try:
            conn.execute(f"ALTER TABLE {table} ADD COLUMN {col_def}")
            conn.commit()
        except Exception:
            pass  # Column already exists — safe to ignore


def _migrate_scope_values(conn: sqlite3.Connection) -> None:
    """Convert old conflated scope values to the new two-axis (scope + state) system.

    Old value  → new scope  / new state
    ─────────────────────────────────────
    durable    → global     / durable
    pinned     → global     / pinned
    project    → project    / durable  (unchanged)
    operator   → operator   / durable  (unchanged)
    """
    conn.execute("UPDATE memory_notes SET scope = 'global' WHERE scope = 'durable'")
    conn.execute(
        "UPDATE memory_notes SET scope = 'global', state = 'pinned' WHERE scope = 'pinned'"
    )
    conn.commit()


def _seed_default_projects(conn: sqlite3.Connection) -> None:
    """Insert the default project list (idempotent — skips existing names)."""
    for name, description in _DEFAULT_PROJECTS:
        existing = conn.execute(
            "SELECT id FROM projects WHERE name = ?", (name,)
        ).fetchone()
        if not existing:
            conn.execute(
                "INSERT INTO projects (id, name, description, status, created_at) VALUES (?, ?, ?, 'active', ?)",
                (str(uuid.uuid4()), name, description, _now()),
            )
    conn.commit()


# ---------------------------------------------------------------------------
# Projects
# ---------------------------------------------------------------------------

def create_project(
    conn: sqlite3.Connection,
    name: str,
    description: str = None,
    status: str = "active",
) -> str:
    project_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO projects (id, name, description, status, created_at) VALUES (?, ?, ?, ?, ?)",
        (project_id, name, description, status, _now()),
    )
    conn.commit()
    return project_id


def get_projects(conn: sqlite3.Connection, status: str = None) -> list[dict]:
    if status:
        cursor = conn.execute(
            "SELECT id, name, description, status, created_at, last_used_at FROM projects WHERE status = ? ORDER BY name ASC",
            (status,),
        )
    else:
        cursor = conn.execute(
            "SELECT id, name, description, status, created_at, last_used_at FROM projects ORDER BY name ASC"
        )
    return [dict(row) for row in cursor.fetchall()]


def get_project(conn: sqlite3.Connection, project_id: str) -> dict | None:
    row = conn.execute(
        "SELECT id, name, description, status, created_at, last_used_at FROM projects WHERE id = ?",
        (project_id,),
    ).fetchone()
    return dict(row) if row else None


def update_project(
    conn: sqlite3.Connection,
    project_id: str,
    name: str = None,
    description: str = None,
    status: str = None,
) -> None:
    if name is not None:
        conn.execute("UPDATE projects SET name = ? WHERE id = ?", (name, project_id))
    if description is not None:
        conn.execute("UPDATE projects SET description = ? WHERE id = ?", (description, project_id))
    if status is not None:
        conn.execute("UPDATE projects SET status = ? WHERE id = ?", (status, project_id))
    conn.commit()


def update_project_last_used(conn: sqlite3.Connection, project_id: str) -> None:
    conn.execute("UPDATE projects SET last_used_at = ? WHERE id = ?", (_now(), project_id))
    conn.commit()


# ---------------------------------------------------------------------------
# Sessions
# ---------------------------------------------------------------------------

def create_session(
    conn: sqlite3.Connection,
    session_id: str,
    model: str = None,
    mode: str = None,
    name: str = None,
    project_id: str = None,
) -> str:
    """Insert a new session row (no-op if the session already exists)."""
    conn.execute(
        "INSERT OR IGNORE INTO sessions (id, name, model, mode, created_at, project_id) VALUES (?, ?, ?, ?, ?, ?)",
        (session_id, name, model, mode, _now(), project_id),
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
    project_id: str = None,
) -> str:
    """Record a file that was ingested in this session."""
    file_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO files (id, session_id, path, content, ingested_at, project_id) VALUES (?, ?, ?, ?, ?, ?)",
        (file_id, session_id, path, content, _now(), project_id),
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
    scope: str = "global",
    state: str = "durable",
    project_id: str = None,
    confidence: float = 1.0,
    source: str = None,
    source_session_id: str = None,
    source_document_id: str = None,
    source_chunk_id: str = None,
    generated_by_model: str = None,
    origin_note_id: str = None,
    # Entity anchor fields — preserve identity context extracted during research
    game: str = None,
    entity_class: str = None,
    build_topic: str = None,
    season: str = None,
    note_type: str = None,
    patch_sensitive: bool = False,
) -> str:
    """Promote a fact into long-term memory with full provenance."""
    note_id = str(uuid.uuid4())
    conn.execute(
        """INSERT INTO memory_notes
           (id, content, tags, scope, state, project_id, created_at, promoted_at,
            confidence, source, source_session_id, source_document_id, source_chunk_id,
            generated_by_model, origin_note_id,
            game, entity_class, build_topic, season, note_type, patch_sensitive)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
        (note_id, content, tags, scope, state, project_id, _now(), _now(),
         confidence, source, source_session_id, source_document_id, source_chunk_id,
         generated_by_model, origin_note_id,
         game, entity_class, build_topic, season, note_type, int(patch_sensitive)),
    )
    conn.commit()
    return note_id


def get_memory_notes(
    conn: sqlite3.Connection,
    scope: str = None,
    state: str = None,
    project_id: str = None,
    exclude_states: list[str] = None,
) -> list[dict]:
    """Return memory notes with optional filtering on scope, state, and project.

    Both axes (scope and state) are independently filterable.
    Notes are returned sorted by usage_count DESC, confidence DESC, created_at ASC.
    """
    conditions = []
    params: list = []
    if scope is not None:
        conditions.append("scope = ?")
        params.append(scope)
    if state is not None:
        conditions.append("state = ?")
        params.append(state)
    if project_id is not None:
        conditions.append("project_id = ?")
        params.append(project_id)
    if exclude_states:
        placeholders = ",".join("?" * len(exclude_states))
        conditions.append(f"state NOT IN ({placeholders})")
        params.extend(exclude_states)
    where = f"WHERE {' AND '.join(conditions)}" if conditions else ""
    cursor = conn.execute(
        f"""SELECT id, content, tags, scope, state, project_id, created_at,
                   confidence, last_used_at, usage_count, source, generated_by_model,
                   game, entity_class, build_topic, season, note_type, patch_sensitive
            FROM memory_notes {where}
            ORDER BY usage_count DESC, confidence DESC, created_at ASC""",
        params,
    )
    return [dict(row) for row in cursor.fetchall()]


def delete_memory_note(conn: sqlite3.Connection, note_id: str) -> None:
    """Permanently remove a memory note by ID."""
    conn.execute("DELETE FROM memory_notes WHERE id = ?", (note_id,))
    conn.commit()


def archive_memory_note(conn: sqlite3.Connection, note_id: str) -> None:
    """Set a note's state to 'archived' (soft delete — preserves provenance)."""
    conn.execute("UPDATE memory_notes SET state = 'archived' WHERE id = ?", (note_id,))
    conn.commit()


def update_memory_note_state(conn: sqlite3.Connection, note_id: str, state: str) -> None:
    conn.execute("UPDATE memory_notes SET state = ? WHERE id = ?", (state, note_id))
    conn.commit()


def update_memory_note(
    conn: sqlite3.Connection,
    note_id: str,
    content: str = None,
    scope: str = None,
    state: str = None,
    confidence: float = None,
    tags: str = None,
) -> None:
    """Partial update of any editable fields on a memory note."""
    fields: dict = {}
    if content is not None:
        fields["content"] = content
    if scope is not None:
        fields["scope"] = scope
    if state is not None:
        fields["state"] = state
    if confidence is not None:
        fields["confidence"] = confidence
    if tags is not None:
        fields["tags"] = tags
    if not fields:
        return
    set_clause = ", ".join(f"{k} = ?" for k in fields)
    conn.execute(
        f"UPDATE memory_notes SET {set_clause} WHERE id = ?",
        [*fields.values(), note_id],
    )
    conn.commit()


def touch_memory_note(conn: sqlite3.Connection, note_id: str) -> None:
    """Record that a note was retrieved: update last_used_at and increment usage_count."""
    conn.execute(
        "UPDATE memory_notes SET last_used_at = ?, usage_count = usage_count + 1 WHERE id = ?",
        (_now(), note_id),
    )
    conn.commit()


# ---------------------------------------------------------------------------
# Documents
# ---------------------------------------------------------------------------

def add_document(
    conn: sqlite3.Connection,
    project_id: str | None,
    filename: str,
    file_hash: str = None,
    file_path: str = None,
    doc_type: str = "markdown",
    raw_content: str = None,
    authority_level: str = "Informational",
    document_type: str = "other",
    finality: str = "final",
) -> str:
    doc_id = str(uuid.uuid4())
    conn.execute(
        """INSERT INTO documents
               (id, project_id, filename, file_hash, file_path, doc_type,
                ingested_at, chunk_count, raw_content,
                authority_level, document_type, finality)
           VALUES (?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?)""",
        (doc_id, project_id, filename, file_hash, file_path, doc_type,
         _now(), raw_content, authority_level, document_type, finality),
    )
    conn.commit()
    return doc_id


def get_documents(
    conn: sqlite3.Connection,
    project_id: str = None,
    global_only: bool = False,
) -> list[dict]:
    cols = "id, project_id, filename, file_hash, doc_type, ingested_at, chunk_count, authority_level, document_type, finality"
    if global_only:
        cursor = conn.execute(
            f"SELECT {cols} FROM documents WHERE project_id IS NULL ORDER BY ingested_at DESC"
        )
    elif project_id:
        cursor = conn.execute(
            f"SELECT {cols} FROM documents WHERE project_id = ? ORDER BY ingested_at DESC",
            (project_id,),
        )
    else:
        cursor = conn.execute(
            f"SELECT {cols} FROM documents ORDER BY ingested_at DESC"
        )
    return [dict(row) for row in cursor.fetchall()]


def add_document_chunk(
    conn: sqlite3.Connection,
    document_id: str,
    project_id: str,
    chunk_index: int,
    heading_path: str,
    chunk_text: str,
) -> str:
    chunk_id = str(uuid.uuid4())
    conn.execute(
        """INSERT INTO document_chunks (id, document_id, project_id, chunk_index, heading_path, chunk_text, created_at)
           VALUES (?, ?, ?, ?, ?, ?, ?)""",
        (chunk_id, document_id, project_id, chunk_index, heading_path, chunk_text, _now()),
    )
    return chunk_id


def get_document_chunks(conn: sqlite3.Connection, document_id: str) -> list[dict]:
    cursor = conn.execute(
        "SELECT id, chunk_index, heading_path, chunk_text FROM document_chunks WHERE document_id = ? ORDER BY chunk_index ASC",
        (document_id,),
    )
    return [dict(row) for row in cursor.fetchall()]


def update_document_chunk_count(
    conn: sqlite3.Connection, document_id: str, chunk_count: int
) -> None:
    conn.execute(
        "UPDATE documents SET chunk_count = ? WHERE id = ?", (chunk_count, document_id)
    )
    conn.commit()


def delete_document(conn: sqlite3.Connection, doc_id: str) -> None:
    """Governed cascading deletion of a document and all its ingestion artifacts.

    Deletion order (FK-safe, foreign_keys=ON):
      1. Embeddings for this document's chunks  (soft ref — no FK)
      2. ingestion_jobs for this document       (hard FK, no CASCADE defined)
      3. document_chunks                        (hard FK, ON DELETE CASCADE would
                                                 also fire, but explicit is safer)
      4. document row itself

    Durable memory notes that were promoted from this document are PRESERVED.
    Only their provenance links (source_document_id / source_chunk_id) are
    nulled out so the notes remain valid standalone knowledge.
    """
    # 1. Collect chunk IDs for embedding cleanup before chunks are gone
    chunk_ids = [row["id"] for row in get_document_chunks(conn, doc_id)]

    # 2. Delete retrieval embeddings (soft reference — not a FK, but must go first
    #    so the search index stays consistent)
    if chunk_ids:
        placeholders = ",".join("?" * len(chunk_ids))
        conn.execute(
            f"DELETE FROM embeddings WHERE source_id IN ({placeholders})"
            f" AND source_type = 'document_chunk'",
            chunk_ids,
        )

    # 3. Sever provenance links on durable memory notes — preserve the notes
    #    themselves (operator-approved synthesized knowledge must survive source
    #    document deletion)
    conn.execute(
        "UPDATE memory_notes SET source_document_id = NULL WHERE source_document_id = ?",
        (doc_id,),
    )
    if chunk_ids:
        placeholders = ",".join("?" * len(chunk_ids))
        conn.execute(
            f"UPDATE memory_notes SET source_chunk_id = NULL"
            f" WHERE source_chunk_id IN ({placeholders})",
            chunk_ids,
        )

    # 4. Remove ingestion job records (hard FK, no ON DELETE CASCADE)
    conn.execute("DELETE FROM ingestion_jobs WHERE document_id = ?", (doc_id,))

    # 5. Remove chunks (ON DELETE CASCADE would also fire on the next step, but
    #    doing it explicitly keeps the order deterministic)
    conn.execute("DELETE FROM document_chunks WHERE document_id = ?", (doc_id,))

    # 6. Remove the document row — all dependents are gone
    conn.execute("DELETE FROM documents WHERE id = ?", (doc_id,))
    conn.commit()


def move_document_project(
    conn: sqlite3.Connection,
    doc_id: str,
    new_project_id: str | None,
) -> bool:
    """Move a document (and all its ingestion artifacts) to a different project
    or to global/shared scope (new_project_id=None).

    Updates project_id on:
      - documents
      - document_chunks  (search uses chunk-level project_id for filtering)
      - embeddings        (semantic search filters on this column)
      - ingestion_jobs    (housekeeping)

    Durable memory notes that were promoted from this document are NOT moved —
    they belong to the knowledge graph, not to the source document lifecycle.

    Returns True if the document was found and moved, False if not found.
    """
    row = conn.execute("SELECT id FROM documents WHERE id = ?", (doc_id,)).fetchone()
    if not row:
        return False

    chunk_ids = [r["id"] for r in get_document_chunks(conn, doc_id)]

    # Update document row
    conn.execute(
        "UPDATE documents SET project_id = ? WHERE id = ?",
        (new_project_id, doc_id),
    )
    # Update chunk project_ids
    conn.execute(
        "UPDATE document_chunks SET project_id = ? WHERE document_id = ?",
        (new_project_id, doc_id),
    )
    # Update embedding project_ids (soft reference via source_id + source_type)
    if chunk_ids:
        placeholders = ",".join("?" * len(chunk_ids))
        conn.execute(
            f"UPDATE embeddings SET project_id = ? WHERE source_type = 'document_chunk'"
            f" AND source_id IN ({placeholders})",
            [new_project_id, *chunk_ids],
        )
    # Update ingestion job project_ids
    conn.execute(
        "UPDATE ingestion_jobs SET project_id = ? WHERE document_id = ?",
        (new_project_id, doc_id),
    )
    conn.commit()
    return True


# ---------------------------------------------------------------------------
# Ingestion jobs
# ---------------------------------------------------------------------------

def create_ingestion_job(
    conn: sqlite3.Connection,
    document_id: str,
    project_id: str = None,
) -> str:
    job_id = str(uuid.uuid4())
    conn.execute(
        """INSERT INTO ingestion_jobs (id, document_id, project_id, status, created_at)
           VALUES (?, ?, ?, 'queued', ?)""",
        (job_id, document_id, project_id, _now()),
    )
    conn.commit()
    return job_id


def claim_next_job(conn: sqlite3.Connection) -> dict | None:
    """Atomically claim the next queued ingestion job. Returns the job dict or None."""
    row = conn.execute(
        "SELECT id, document_id, project_id FROM ingestion_jobs WHERE status = 'queued' ORDER BY created_at ASC LIMIT 1"
    ).fetchone()
    if not row:
        return None
    conn.execute(
        "UPDATE ingestion_jobs SET status = 'processing', started_at = ? WHERE id = ?",
        (_now(), row["id"]),
    )
    conn.commit()
    return dict(row)


def update_ingestion_job(
    conn: sqlite3.Connection,
    job_id: str,
    status: str,
    chunk_count: int = None,
    error_message: str = None,
) -> None:
    completed_at = _now() if status in ("completed", "failed") else None
    conn.execute(
        """UPDATE ingestion_jobs
           SET status = ?, chunk_count = COALESCE(?, chunk_count),
               error_message = ?, completed_at = COALESCE(?, completed_at)
           WHERE id = ?""",
        (status, chunk_count, error_message, completed_at, job_id),
    )
    conn.commit()


def get_ingestion_job_for_document(
    conn: sqlite3.Connection, document_id: str
) -> dict | None:
    row = conn.execute(
        "SELECT id, status, chunk_count, started_at, completed_at, error_message FROM ingestion_jobs WHERE document_id = ? ORDER BY created_at DESC LIMIT 1",
        (document_id,),
    ).fetchone()
    return dict(row) if row else None


# ---------------------------------------------------------------------------
# Research cache
# ---------------------------------------------------------------------------

def save_research_cache(
    conn: sqlite3.Connection,
    session_id: str,
    project_id: str,
    query: str,
    raw_result: str,
    candidate_notes: list[dict],
    trace: dict = None,
    model: str = None,
) -> str:
    cache_id = str(uuid.uuid4())
    conn.execute(
        """INSERT INTO research_cache
               (id, session_id, project_id, query, raw_result, candidate_notes,
                trace_json, model, state, created_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'pending', ?)""",
        (
            cache_id, session_id, project_id, query, raw_result,
            json.dumps(candidate_notes),
            json.dumps(trace) if trace else None,
            model,
            _now(),
        ),
    )
    conn.commit()
    return cache_id


def get_pending_research(
    conn: sqlite3.Connection, project_id: str = None
) -> list[dict]:
    if project_id:
        cursor = conn.execute(
            "SELECT id, session_id, project_id, query, raw_result, candidate_notes, created_at FROM research_cache WHERE state = 'pending' AND project_id = ? ORDER BY created_at DESC",
            (project_id,),
        )
    else:
        cursor = conn.execute(
            "SELECT id, session_id, project_id, query, raw_result, candidate_notes, created_at FROM research_cache WHERE state = 'pending' ORDER BY created_at DESC"
        )
    rows = []
    for row in cursor.fetchall():
        d = dict(row)
        if d.get("candidate_notes"):
            d["candidate_notes"] = json.loads(d["candidate_notes"])
        rows.append(d)
    return rows


def update_research_state(conn: sqlite3.Connection, cache_id: str, state: str) -> None:
    conn.execute(
        "UPDATE research_cache SET state = ?, reviewed_at = ? WHERE id = ?",
        (state, _now(), cache_id),
    )
    conn.commit()


def get_all_research(
    conn: sqlite3.Connection,
    project_id: str = None,
    limit: int = 50,
) -> list[dict]:
    """Return research runs of any state, newest first — for the Research Trace tab.
    Excludes raw_result to keep payloads small; use get_research_by_id for the full record."""
    if project_id:
        cursor = conn.execute(
            "SELECT id, session_id, project_id, query, candidate_notes, trace_json, model, state, created_at, reviewed_at "
            "FROM research_cache WHERE project_id = ? ORDER BY created_at DESC LIMIT ?",
            (project_id, limit),
        )
    else:
        cursor = conn.execute(
            "SELECT id, session_id, project_id, query, candidate_notes, trace_json, model, state, created_at, reviewed_at "
            "FROM research_cache ORDER BY created_at DESC LIMIT ?",
            (limit,),
        )
    rows = []
    for row in cursor.fetchall():
        d = dict(row)
        if d.get("candidate_notes"):
            d["candidate_notes"] = json.loads(d["candidate_notes"])
        if d.get("trace_json"):
            d["trace_json"] = json.loads(d["trace_json"])
        rows.append(d)
    return rows


def get_research_by_id(conn: sqlite3.Connection, cache_id: str) -> dict | None:
    """Return the full research cache record including raw_result, for the trace detail view."""
    row = conn.execute(
        "SELECT id, session_id, project_id, query, raw_result, candidate_notes, "
        "trace_json, model, state, created_at, reviewed_at "
        "FROM research_cache WHERE id = ?",
        (cache_id,),
    ).fetchone()
    if not row:
        return None
    d = dict(row)
    if d.get("candidate_notes"):
        d["candidate_notes"] = json.loads(d["candidate_notes"])
    if d.get("trace_json"):
        d["trace_json"] = json.loads(d["trace_json"])
    return d


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
    project_id: str = None,
) -> str:
    """Embed a chunk of text and persist the vector to the database."""
    vector = get_embedding(chunk_text, host)
    embedding_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO embeddings (id, source_id, source_type, chunk_text, vector, project_id, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
        (embedding_id, source_id, source_type, chunk_text, json.dumps(vector), project_id, _now()),
    )
    conn.commit()
    return embedding_id


def search_memory(
    conn: sqlite3.Connection,
    query_vector: list[float],
    top_k: int = 3,
    source_type: str = None,
    project_id: str = None,
) -> list[dict]:
    """Return the top-k most similar chunks by cosine similarity."""
    conditions = []
    params: list = []
    if source_type:
        conditions.append("source_type = ?")
        params.append(source_type)
    if project_id:
        # Include global (project_id IS NULL) docs in every project search so
        # shared reference documents surface regardless of which project is active.
        conditions.append("(project_id = ? OR project_id IS NULL)")
        params.append(project_id)
    where = f"WHERE {' AND '.join(conditions)}" if conditions else ""
    cursor = conn.execute(
        f"SELECT id, source_id, source_type, chunk_text, vector FROM embeddings {where}",
        params,
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
    project_id: str = None,
) -> str:
    """Persist a rolling summary for a session."""
    summary_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO session_summaries (id, session_id, summary, covers_up_to_message_id, created_at, project_id) VALUES (?, ?, ?, ?, ?, ?)",
        (summary_id, session_id, summary, covers_up_to_message_id, _now(), project_id),
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

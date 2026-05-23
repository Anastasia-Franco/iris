# IRIS Development Update — May 23, 2026

## Major Architectural Shift

IRIS transitioned from:

```text
MSI Workstation
├── WinForms UI
├── Python orchestration
├── SQLite memory
└── Local context handling
```

to:

```text
Clients
├── MSI WinForms UI
├── Future Phone App
├── Laptop / Tablet
└── Mesh/VPN Connected Devices
        ↓
IRIS Ubuntu Server
├── FastAPI orchestration layer
├── SQLite persistent memory
├── Context injection engine
├── Embedding generation
├── Session management
├── Ollama inference
└── Project-aware memory
```

This establishes IRIS as a centralized local intelligence server rather than a desktop-only orchestration tool.

---

# Infrastructure Changes

## Ubuntu IRIS Server Deployment

Created and deployed:

* `iris_server.py`
* `iris_memory.py`
* `requirements.txt`
* `iris-api.service`

Moved infrastructure into:

```text
/opt/iris/
```

including:

* virtual environment
* memory database
* API server
* orchestration logic

Systemd service created:

```text
iris-api.service
```

to persist the IRIS API server across reboots.

---

# Memory Architecture Evolution

## Previous State

Memory was:

* local to MSI
* tied to desktop execution
* inaccessible remotely

## Current State

Memory now lives centrally on IRIS.

This allows:

* persistent sessions across devices
* future mobile app support
* mesh/VPN remote continuity
* centralized project memory
* durable organizational intelligence

---

# Multi-Tier Memory System

IRIS now injects structured context layers into prompts:

```text
[SYSTEM IDENTITY]
[PROJECT MEMORY]
[PINNED FACTS]
[SESSION SUMMARY]
[RECENT CONVERSATION]
```

This replaced simple chat replay with layered contextual reasoning.

---

# New Memory Capabilities

## Session Persistence

* Conversations persist across requests
* Session IDs maintained client-side
* Context reconstructed dynamically

## Project Memory

Seeded documents can now become persistent project-aware memory.

Example:

* IRIS architecture docs
* infrastructure notes
* operational standards
* organizational references

## Durable Memory

Promoted facts persist independently of sessions.

## Session Summaries

Long conversations can now be compressed into persistent summaries.

## Background Embeddings

Embeddings now generate asynchronously using:

```text
nomic-embed-text
```

without blocking UI responsiveness.

---

# UI Improvements

## RichTextBox Rendering

Migrated from raw TextBox output to RichTextBox rendering.

Benefits:

* improved readability
* markdown-aware formatting
* better long-session usability
* scalable conversation display

## Session Controls

Added:

* persistent session IDs
* “New Session” support
* continuous conversation accumulation

---

# FastAPI API Layer

IRIS now exposes a centralized orchestration API.

Planned endpoints include:

```text
POST /chat
GET /health
GET /sessions
POST /memory/promote
POST /memory/seed
```

This API layer is foundational for:

* mobile clients
* remote orchestration
* mesh-connected devices
* future distributed workflows

---

# Ollama Architecture Clarification

Clarified distinction between:

* bind addresses (`0.0.0.0`)
* client connection addresses (`127.0.0.1`)

Correct local IRIS configuration:

```env
OLLAMA_HOST=http://127.0.0.1:11434
```

because:

* FastAPI and Ollama both run locally on IRIS
* `0.0.0.0` is valid for server binding, not client requests

---

# Identity Layer Realization

Discovered that the base model still defaults to pretrained hosted identity behaviors.

Example:

* claiming to be cloud-hosted
* identifying as Tongyi Lab infrastructure
* denying local persistent memory

This led to recognition that IRIS requires a persistent orchestration-level system identity injected before contextual memory.

This separates:

* base model identity
  from
* orchestration identity

A major conceptual milestone in IRIS development.

---

# Conceptual Evolution

IRIS is no longer functioning as:

* a stateless local chatbot
* a simple UI wrapper around Ollama

IRIS is evolving into:

* a persistent intelligence infrastructure
* a project-aware reasoning environment
* a continuity-preserving orchestration system
* a local-first cognitive augmentation platform

Core philosophy clarified:

> IRIS is intended to help think, remember, theorize, and maintain continuity — not replace human judgment or cognition.

---

# Current Technical Status

## Working

* Centralized memory on IRIS
* FastAPI orchestration
* Session persistence
* Multi-tier context injection
* Background embeddings
* RichTextBox rendering
* Streaming responses
* Systemd-managed API service

## In Progress

* System identity injection
* Final API stabilization
* Remote client testing
* Phone app planning
* Project-scoped memory expansion

---

# Strategic Direction

The architecture is now aligned toward:

```text
Local-first
Persistent
Project-aware
Mesh-accessible
Self-hosted
Operator-controlled
Long-term continuity
```

rather than:

* cloud dependency
* stateless interaction
* disposable conversations
* centralized hosted AI paradigms

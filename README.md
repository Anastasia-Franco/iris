# IRIS

**Independent Resilient Intelligence System**

IRIS is a local-first AI assistant and orchestration system designed for:
- document analysis
- research workflows
- project-aware assistance
- local/private inference
- multi-model AI routing
- developer and organizational workflows

IRIS is built around a lightweight CLI architecture using:
- Ollama
- local LLMs
- streaming inference
- file ingestion
- persistent project context

## Philosophy

IRIS is designed around a simple principle:

> The UI is not the system.

The core system is:
- local inference
- orchestration
- context handling
- tooling
- privacy-first workflows

## Current Features

- Remote Ollama inference
- Streaming CLI responses
- File/document ingestion
- Multi-model support
- Local-first architecture

## Planned Features

- Project profiles
- Persistent sessions
- Research mode
- Web-assisted synthesis
- Memory/context storage
- Tool adapters
- Multi-document workflows

## Example Usage

```powershell
iris "Summarize this document" .\README.md
```

```powershell
iris "Compare these files" .\doc1.md .\doc2.md
```

## Requirements

- Python 3.11+
- Ollama
- Local LLM models

## Status

Early development / active experimentation. Please report any issues or feedback!
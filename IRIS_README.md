# Independent Resilient Intelligence System (IRIS)

## What is IRIS?

IRIS — the **Independent Resilient Intelligence System** — is a local-first AI infrastructure and orchestration project designed to provide powerful, private, and self-hosted intelligence capabilities without relying on cloud AI subscriptions or external providers.

IRIS is designed around a simple principle:

> The UI is not the system.

The system is the orchestration layer:
- local inference
- context handling
- memory
- tooling
- document analysis
- research workflows
- project-aware assistance

IRIS is intended to function as a long-term personal and organizational intelligence platform that remains:
- private
- inspectable
- portable
- durable
- fully controlled by its operator

---

# Mission

IRIS exists to:
- replace dependency on hosted AI subscriptions where practical
- provide secure and local AI-assisted workflows
- support coding, research, writing, and organizational infrastructure
- maintain full control over data, memory, and inference systems
- create an extensible open local AI architecture

---

# Hardware Architecture

IRIS currently operates as a dual-node local AI environment connected through a dedicated high-speed bridge network.

## IRIS Node
Primary inference and reasoning system.

Current responsibilities:
- Ollama inference server
- large-context reasoning
- embedding generation
- future memory infrastructure
- research orchestration
- multi-model routing

Current hardware:
- AMD Ryzen AI MAX+ platform
- 128GB unified memory
- 32-core processing environment
- high-speed NVMe storage

## MSI Workstation
Primary user interaction and orchestration workstation.

Current responsibilities:
- VS Code development
- Continue.dev integration
- CLI orchestration
- document workflows
- Git/repository management
- user interface and operational tooling

The two systems are connected using a dedicated Thunderbolt/USB4 peer-to-peer bridge operating on the `10.10.10.x` subnet.

This architecture allows the workstation to function as a lightweight controller while inference workloads execute remotely on the IRIS node with extremely low latency.

---

# Current Model Architecture

IRIS currently operates as a multi-model local inference environment using Ollama.

## Operational Models

### Qwen 2.5 Coder 7B
Primary fast-response operational model.

Used for:
- coding assistance
- CLI interaction
- document analysis
- rewriting workflows
- project assistance
- rapid iteration tasks

### DeepSeek R1 32B / 70B
High-reasoning models used for advanced workflows.

Used for:
- architectural reasoning
- complex synthesis
- multi-document analysis
- research workflows
- long-context tasks

### nomic-embed-text
Embedding and retrieval model.

Used for:
- context indexing
- retrieval pipelines
- semantic search
- future vector memory systems

---

# Current Tooling

IRIS currently integrates with:
- Ollama
- Continue.dev
- VS Code
- local CLI tooling
- streaming inference workflows

The current IRIS CLI supports:
- remote local inference
- streaming responses
- file ingestion
- multi-file analysis
- document summarization

---

# Design Philosophy

IRIS is intentionally:
- local-first
- open development
- modular
- inspectable
- infrastructure-aware
- privacy-focused

The project prioritizes:
- low latency
- self-hosting
- data sovereignty
- reproducibility
- operator control

IRIS is not designed as a generalized chatbot platform. It is intended to function as a durable intelligence and orchestration environment.

---

# Planned Features

Planned development areas include:
- persistent memory systems
- project profiles
- research mode
- Tavily/web-assisted workflows
- local vector databases
- SQL-backed memory systems
- multi-document synthesis
- organizational workflow tooling
- tool adapters and automation systems

---

# Why IRIS Exists

IRIS was built to address several practical concerns with hosted AI systems:

## Privacy
To ensure sensitive code, documents, organizational data, and research never leave the local network.

## Performance
To leverage local hardware, unified memory, and high-speed storage for fast and unrestricted inference.

## Ownership
To maintain complete control over models, workflows, memory, and infrastructure.

## Transparency
To develop AI tooling openly and inspectably rather than relying on opaque external systems.

---

# Project Status

IRIS is currently in active development and experimentation.

The project is evolving iteratively through real-world usage rather than being designed as a theoretical framework.
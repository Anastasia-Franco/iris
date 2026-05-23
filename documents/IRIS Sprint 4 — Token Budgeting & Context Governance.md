````md
# IRIS Sprint 4 — Token Budgeting & Context Governance

## Overview

IRIS now includes explicit token budget allocation and inspectable context governance layers.

Unlike traditional AI systems that silently truncate prompts or hide retrieval behavior, IRIS exposes how cognition resources are allocated during prompt construction.

This allows:
- predictable context behavior
- operator transparency
- retrieval tuning
- governance-aware prompt construction
- explainable memory prioritization

The token budget system is part of the larger IRIS philosophy:

> cognition should be inspectable and governable.

---

# Current Token Allocation Model

Default maximum context:

```text
6000 tokens
````

Current allocation policy:

| Layer                     | Allocation          |
| ------------------------- | ------------------- |
| System Identity           | Reserved / uncapped |
| Operator Notes            | 10%                 |
| Project Memory            | 25%                 |
| Global Memory             | 10%                 |
| Retrieved Document Chunks | 25%                 |
| Session Summary           | 10%                 |
| Recent Conversation       | 20%                 |

---

# Governance Philosophy

The token hierarchy reflects intentional governance priorities.

IRIS prioritizes:

1. System identity stability
2. Operator governance
3. Project-specific memory
4. Retrieved semantic knowledge
5. Session continuity
6. Conversational recency

This prevents:

* conversational drift
* retrieval dominance
* memory pollution
* hidden truncation behavior
* accidental project leakage

---

# Inspectable Cognition

IRIS now exposes:

* token allocation percentages
* context injection ordering
* retrieved memory layers
* retrieval chunk attribution
* project-scoped context boundaries

The objective is to allow the operator to answer:

> “Why did IRIS answer this way?”

instead of relying on opaque prompt construction.

---

# Long-Term Direction

The token governance layer establishes the foundation for:

* adaptive retrieval weighting
* project-aware prioritization
* memory confidence tuning
* archival memory decay
* operator-defined context policies
* future multi-agent orchestration

This architecture treats memory as governed infrastructure rather than passive storage.

---

# Architectural Significance

This moves IRIS beyond:

* stateless chat
* hidden prompt assembly
* opaque retrieval systems

toward:

> inspectable cognition orchestration.

The system is intentionally designed to behave more like an operational intelligence environment than a consumer AI chatbot.

````
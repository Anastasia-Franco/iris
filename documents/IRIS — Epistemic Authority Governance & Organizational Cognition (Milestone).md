# IRIS — Epistemic Authority Governance & Organizational Cognition (Milestone)

## Overview

IRIS has now crossed beyond:

* local AI chat
* basic RAG
* “chat with documents”

into an early-stage **governed organizational cognition system**.

This milestone focused on solving a fundamental problem discovered during live corpus testing with real Critical Resistance organizational documents:

```text
retrieval relevance ≠ epistemic authority
```

The system previously treated:

* published organizational framework documents
* strategic planning drafts
* meeting-style synthesis
* anecdotal comments

as having roughly equal synthesis weight once retrieved.

This produced two dangerous failure modes:

1. anecdotal/contextual material becoming over-weighted
2. legitimate organizational framework reasoning becoming under-weighted

The new governance layer introduces authority-aware synthesis and bounded contextual reasoning.

---

# Major Architectural Shift

## Before

IRIS behavior resembled:

```text
retrieve relevant chunks
→ synthesize equally
→ answer
```

This produced:

* contextual flattening
* weak authority separation
* overly rigid extraction OR unsafe synthesis
* poor organizational nuance retention

---

## After

IRIS now models:

```text
Operator Correction
    ↓
Durable Project Memory
    ↓
Authoritative Source Material
    ↓
Contextual Retrieval
    ↓
Bounded Model Inference
```

This creates:

* inspectable synthesis
* authority-aware reasoning
* organizational continuity
* corrigible cognition

---

# Epistemic Authority Governance Layer

## New Document Metadata

Documents now carry governance metadata:

### Authority Levels

* Definitive
* Authoritative
* Informational
* Contextual
* Anecdotal

### Document Types

* published_framework
* operational_guide
* strategic_draft
* meeting_notes
* planning_discussion
* other

### Finality

* final
* draft
* provisional

This allows IRIS to distinguish:

* organizational identity/framework
  from:
* operational context
  from:
* anecdotal planning discussion

---

# Grounding System Rewrite

The previous `[GROUNDING RULES]` block was replaced with:

```text
[EPISTEMIC AUTHORITY RULES]
```

The new hierarchy explicitly distinguishes:

## Layer 3

### Durable Organizational Cognition

Operator-approved project memory and validated synthesis.

## Layer 5

### Retrieved Source Material

Variable-authority retrieval substrate.

This distinction is critical:

```text
documents are not durable cognition
documents are source material for cognition formation
```

---

# Bounded Contextual Reasoning

One of the largest discoveries:

Rigid grounding suppressed legitimate organizational reasoning.

Example:

* intersectionality
* decolonization
* structural oppression analysis

were being omitted unless explicitly retrieved word-for-word.

The new rules now allow:

```text
where the organizational corpus establishes a framework,
bounded contextual reasoning extending that framework
is permitted with explicit labeling
```

This restores:

* contextual synthesis
* organizational coherence
* political nuance

WITHOUT enabling:

* unconstrained hallucination
* generic activist interpolation
* ideology fabrication

---

# Retrieval Authority Weighting

Layer 5 retrieval now labels chunks with authority metadata:

Example:

```text
[RETRIEVED SOURCE MATERIAL]
Source: CR-Abolitionist-Toolkit.pdf
Authority: Authoritative
Type: published_framework
```

This changes synthesis behavior significantly.

IRIS can now:

* prioritize framework docs
* suppress anecdotal overweighting
* contextualize planning drafts
* preserve source hierarchy

---

# Durable Memory Philosophy Shift

A major conceptual breakthrough emerged:

## Old Model

```text
documents = memory
```

## New Model

```text
documents = retrieval substrate
durable memory = operator-approved crystallized synthesis
```

This is foundational.

The intended pipeline now becomes:

```text
Raw Documents
    ↓
Retrieval
    ↓
Bounded Synthesis
    ↓
Operator Review
    ↓
Durable Memory Promotion
```

Durable memory should contain:

* stable organizational principles
* recurring strategic understanding
* validated synthesis
* durable framework reasoning

NOT:

* every retrieved chunk
* every planning comment
* every anecdotal observation

---

# PDF Ingestion Infrastructure

IRIS now supports:

```text
PDF → normalized markdown → chunking → embeddings → retrieval
```

Features:

* async ingestion
* chunk queueing
* markdown sidecar generation
* cached normalized representations
* ingestion metadata tracking

This transformed IRIS into a real organizational corpus system.

---

# UI / Workflow Evolution

IRIS now increasingly behaves like:

```text
a lightweight organizational cognition workstation
```

instead of:

```text
a local AI demo
```

## Added

* Copy output
* Save .txt
* Save .rtf
* CSV export foundation
* document authority controls
* document metadata editing
* project-scoped document management
* move document between projects
* streaming orchestration feedback

---

# Rendering / UX Discoveries

Live testing exposed several UI-level cognition issues:

## qwen3:30b `<think>` tags

qwen streaming behavior caused:

* markdown leakage
* rendering instability
* formatting regressions

Filtering and cleanup layers were added.

## RichTextBox limitations

Markdown tables rendered poorly.

Current direction:

* simplified table rendering
* readable fallback formatting
* eventual richer rendering layer later

## Prompt input ergonomics

Long-form prompt engineering now requires:

* scrollable prompt input
* better editing ergonomics
* workflow-oriented interaction

---

# Major Conceptual Discoveries

## 1. Retrieval Quality ≠ Synthesis Quality

A critical realization:
excellent retrieval can still produce poor cognition.

The true challenge is:

* synthesis governance
* authority weighting
* contextual reasoning discipline

---

## 2. Semantic Relevance ≠ Epistemic Authority

Highly relevant chunks are not automatically authoritative.

This became obvious when:

* planning anecdotes
* draft comments
* contextual notes

began surfacing disproportionately in synthesis.

---

## 3. Durable Cognition Must Be Curated

Long-term memory should not be:

* raw retrieval residue
* automatic ingestion
* uncontrolled chunk persistence

Instead:

* durable memory is curated synthesis
* operator-reviewed
* authority-aware
* organizationally stable

---

## 4. Real Organizational Corpora Stress AI Systems Properly

Critical Resistance documents exposed:

* ideological nuance
* strategic ambiguity
* operational layering
* overlapping terminology
* framework continuity
* authority conflicts

far better than synthetic benchmarks.

---

# Current IRIS Identity

IRIS is now evolving toward:

```text
operator-governed longitudinal organizational cognition
```

rather than:

```text
chatbot with memory
```

And the architecture increasingly reflects that distinction.

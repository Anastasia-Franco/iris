# IRIS WinForms Architecture Plan

## Overview

IRIS will use a lightweight native Windows frontend built with PowerShell and WinForms as the orchestration and control layer for local AI infrastructure.

The system is intentionally modular:

* Python handles inference, research, and AI logic
* Ollama handles model execution
* PowerShell handles orchestration and Windows integration
* WinForms provides the native desktop interface

This keeps the stack lightweight, fast, portable, and easy for others to self-host.

The project is intentionally not designed as a cloud SaaS platform. Users will run and control their own infrastructure locally or over trusted mesh networking.

---

# Core Architecture

```text
[ WinForms UI ]
        |
[ PowerShell Controller ]
        |
[ Python Backend (iris.py) ]
        |
[ Ollama API ]
        |
[ Local Models ]
```

---

# Design Goals

## Primary Goals

* Native Windows performance
* Extremely low overhead
* Easy local deployment
* Offline-capable operation
* Modular architecture
* Swappable AI models
* Hardware-aware orchestration
* Mesh-accessible control interface

## Secondary Goals

* Multi-device access over trusted network
* Lightweight remote control
* Extendable plugin/tool system
* Future cross-platform backend compatibility

---

# Why WinForms

WinForms provides:

* Fast development iteration
* Native Windows appearance
* Minimal memory usage
* Easy process execution
* Direct PowerShell integration
* Stable event-driven architecture
* Easy streaming output handling

Compared to Electron or browser-based interfaces:

| Stack     | Overhead      | Complexity | Native Integration |
| --------- | ------------- | ---------- | ------------------ |
| WinForms  | Very Low      | Low        | Excellent          |
| WPF       | Moderate      | Moderate   | Excellent          |
| Electron  | High          | High       | Moderate           |
| Web Stack | Moderate-High | High       | Indirect           |

WinForms is appropriate for rapid development and orchestration tooling.

---

# Backend Responsibilities

## Python (`iris.py`)

Responsible for:

* Prompt handling
* Ollama API communication
* Streaming inference
* Tavily research integration
* File ingestion
* Agent logic
* Research mode execution
* Future async orchestration

The Python layer remains CLI-first.

Example:

```bash
python iris.py --model qwen3:30b "Explain this code"
```

---

# PowerShell Responsibilities

PowerShell acts as the orchestration layer between UI and backend systems.

Responsibilities include:

* Launching Python processes
* Capturing live stdout streams
* Managing model execution
* Reading hardware/system status
* Process lifecycle management
* Configuration management
* Local API control
* Windows-native integrations

Potential future integrations:

* GPU monitoring
* Ollama model manager
* Auto-launch profiles
* Local service management
* Mesh node discovery
* Remote execution controls

---

# Planned WinForms Components

## Main Window

Primary interface container.

Contains:

* Prompt input
* Response output
* Model selection
* Mode selection
* Session controls

---

## Prompt Input Panel

Features:

* Multi-line prompt entry
* File attachment support
* Drag-and-drop support
* Prompt templates

---

## Output Console

Streaming AI output display.

Potential controls:

* RichTextBox
* Scrollback history
* Copy/export support
* Markdown rendering (future)

---

## Model Selector

Dropdown or sidebar allowing:

* Dynamic model switching
* Runtime model loading
* Backend selection
* Context size display

Example models:

* qwen3:30b
* llama3.3
* deepseek-r1:32b
* qwen-coder-next

---

## Mode Controls

Toggle between:

* Prompt mode
* Research mode
* Agent mode
* Debug mode

---

## System Status Panel

Displays:

* Ollama status
* GPU usage
* RAM usage
* Active model
* Process state
* Queue state

---

# Streaming Architecture

Current Python implementation already streams tokens incrementally:

```python
print(data.get("response", ""), end="", flush=True)
```

PowerShell will capture stdout asynchronously and append directly into the UI output window.

This avoids polling and enables real-time token display.

---

# Mesh Networking Direction

IRIS is not intended as a centralized hosted platform.

Future architecture may expose lightweight local APIs over trusted mesh/VPN infrastructure such as:

* Tailscale
* MeshCentral
* WireGuard
* ZeroTier

This allows:

* Laptop access
* Tablet access
* Phone access
* Remote orchestration

without requiring public cloud infrastructure.

Users are expected to self-host their own inference systems.

---

# Security Philosophy

IRIS prioritizes:

* Local-first operation
* Self-hosted control
* Minimal external dependencies
* Explicit network exposure
* User ownership of infrastructure

No centralized hosted dependency should be required for core functionality.

---

# Long-Term Possibilities

Potential future directions:

* WPF frontend migration
* Cross-platform UI layer
* Multi-agent orchestration
* Plugin architecture
* Local vector database integration
* Speech input/output
* Distributed inference coordination
* Shared mesh model pools
* Native updater system

---

# Immediate Development Priorities

## Phase 1

* Basic WinForms shell
* Prompt textbox
* Output console
* Run button
* Model dropdown
* PowerShell → Python process execution
* Streaming stdout support

## Phase 2

* Multi-tab sessions
* Research mode controls
* Config management
* Session persistence
* Hardware monitoring

## Phase 3

* Mesh API layer
* Remote node management
* Distributed orchestration
* Multi-device interface access

---

# Philosophy

IRIS is intended to function more like personal infrastructure than a commercial AI platform.

The system should remain:

* lightweight
* understandable
* modular
* self-hostable
* hardware-aware
* user-controlled

The objective is not mass-scale hosting.

The objective is empowering people to run their own AI infrastructure.

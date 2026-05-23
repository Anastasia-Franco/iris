#!/usr/bin/env python3

import argparse
import requests
import sys
import json
import os
import time
import uuid
import threading
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
from dotenv import load_dotenv
from tavily import TavilyClient
import iris_memory

DEFAULT_HOST = "http://10.10.10.2:11434"
DEFAULT_MODEL = "qwen3:30b"

def tavily_research(query, max_results=5):
    load_dotenv()

    api_key = os.getenv("TAVILY_API_KEY")
    if not api_key:
        raise RuntimeError("Missing TAVILY_API_KEY in .env")

    client = TavilyClient(api_key=api_key)

    return client.search(
        query=query,
        search_depth="advanced",
        max_results=max_results,
        include_answer=True,
        include_raw_content=False,
    )

def ask_tavily(query, max_results=5):
    try:
        results = tavily_research(query, max_results)

        if not results:
            return "No relevant information found."

        response = ""

        if results.get("answer"):
            response += f"IRIS Research Answer:\n\n{results['answer']}\n\n"

        if results.get("results"):
            response += "Sources:\n\n"

            for result in results["results"]:
                response += f"- {result.get('title', 'Untitled')}\n"
                response += f"  {result.get('url', '')}\n"

        return response

    except Exception as e:
        return f"Tavily error: {e}"

def ask_ollama(prompt, host, model):
    url = f"{host.rstrip('/')}/api/generate"

    payload = {
        "model": model,
        "prompt": prompt,
        "stream": True,
        "keep_alive": "30m"
    }

    response = requests.post(url, json=payload, timeout=300, stream=True)
    response.raise_for_status()

    full_response = []
    for line in response.iter_lines():
        if line:
            chunk = line.decode("utf-8")
            try:
                data = json.loads(chunk)
                token = data.get("response", "")
                print(token, end="", flush=True)
                full_response.append(token)
            except ValueError as e:
                print(f"JSON parsing error: {e}", file=sys.stderr)

    print()
    return "".join(full_response)


def _embed_in_background(message_id: str, text: str, host: str) -> None:
    """Embed a message in a fresh DB connection. Runs in a daemon thread."""
    try:
        db = iris_memory.init_db()
        iris_memory.embed_and_store(db, message_id, "message", text, host)
        db.close()
    except Exception:
        pass  # never surface embedding failures to the user

def main():
    parser = argparse.ArgumentParser(description="IRIS CLI wrapper for Ollama")
    parser.add_argument("items", nargs="*", help="Prompt text and optional files")
    parser.add_argument("--host", default=DEFAULT_HOST, help="Ollama host URL")
    parser.add_argument("--model", default=DEFAULT_MODEL, help="Ollama model name")
    parser.add_argument("--mode", choices=["prompt", "research"], default="prompt", help="Specify the mode (default: prompt)")
    parser.add_argument("--session-id", default=None, dest="session_id", help="Session UUID to continue a prior conversation")
    parser.add_argument("--seed-memory", nargs="+", metavar="FILE", dest="seed_memory", help="Seed project memory from one or more files")
    parser.add_argument("--promote", metavar="FACT", help="Promote a fact into long-term memory")
    parser.add_argument("--scope", default="durable", choices=["project", "durable", "pinned", "operator"], help="Memory scope for --promote (default: durable)")
    parser.add_argument("--list-memory", action="store_true", dest="list_memory", help="List all memory notes")
    parser.add_argument("--delete-memory", metavar="ID", dest="delete_memory", help="Delete a memory note by ID")
    parser.add_argument("--summarize-session", metavar="SESSION_ID", dest="summarize_session", help="Generate and store a summary for a session")

    args = parser.parse_args()

    # --- Management commands (standalone, no prompt needed) ---
    if args.seed_memory:
        db = iris_memory.init_db()
        for file_path in args.seed_memory:
            if os.path.isfile(file_path):
                with open(file_path, "r", encoding="utf-8") as f:
                    content = f.read()
                note_id = iris_memory.add_memory_note(db, content, tags=os.path.basename(file_path), scope="project")
                print(f"Seeded [{os.path.basename(file_path)}] \u2192 {note_id}")
            else:
                print(f"File not found: {file_path}", file=sys.stderr)
        db.close()
        return

    if args.promote:
        db = iris_memory.init_db()
        note_id = iris_memory.add_memory_note(db, args.promote, scope=args.scope)
        print(f"Promoted [{args.scope}] \u2192 {note_id}")
        db.close()
        return

    if args.list_memory:
        db = iris_memory.init_db()
        notes = iris_memory.get_memory_notes(db)
        if notes:
            for note in notes:
                preview = note["content"][:80].replace("\n", " ")
                print(f"[{note['scope']:8}] {note['id'][:8]}...  {preview}")
        else:
            print("No memory notes found.")
        db.close()
        return

    if args.delete_memory:
        db = iris_memory.init_db()
        iris_memory.delete_memory_note(db, args.delete_memory)
        print(f"Deleted: {args.delete_memory}")
        db.close()
        return

    if args.summarize_session:
        db = iris_memory.init_db()
        all_msgs = iris_memory.get_all_messages(db, args.summarize_session)
        if not all_msgs:
            print("No messages found for that session.", file=sys.stderr)
            db.close()
            return
        history = "\n".join(f"{'User' if m['role']=='user' else 'IRIS'}: {m['content']}" for m in all_msgs)
        summary_prompt = f"Summarize this conversation in 4-6 concise sentences, capturing the key topics and decisions:\n\n{history}"
        print("Generating summary...", file=sys.stderr)
        summary = ask_ollama(summary_prompt, args.host, args.model)
        last_id = all_msgs[-1]["id"] if all_msgs else None
        iris_memory.save_session_summary(db, args.summarize_session, summary, last_id)
        print(f"\nSummary saved for session {args.summarize_session[:8]}...")
        db.close()
        return

    if args.mode == "prompt":
        items = args.items
        prompt_parts = []
        ingested_files = []

        for item in items:
            if os.path.isfile(item):
                with open(item, "r", encoding="utf-8") as f:
                    file_content = f.read()
                    prompt_parts.append(f"""
FILE: {item}
{file_content}
""")
                    ingested_files.append((item, file_content))
            else:
                prompt_parts.append(item)

        prompt = " ".join(prompt_parts).strip()

        if not prompt:
            print("No prompt provided.", file=sys.stderr)
            sys.exit(1)

        # --- Memory: open DB, ensure session exists ---
        session_id = args.session_id or str(uuid.uuid4())
        db = iris_memory.init_db()
        iris_memory.create_session(db, session_id, model=args.model, mode=args.mode)

        for file_path, file_content in ingested_files:
            iris_memory.save_file_reference(db, session_id, file_path, file_content)

        # --- Assemble multi-tier context ---
        context_parts = []

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

        summary_text, recent = iris_memory.get_context_with_budget(db, session_id, max_tokens=3000, recent_n=8)
        if summary_text:
            context_parts.append(f"[SESSION SUMMARY]\n{summary_text}")

        if recent:
            history_lines = []
            for msg in recent:
                label = "User" if msg["role"] == "user" else "IRIS"
                history_lines.append(f"{label}: {msg['content']}")
            context_parts.append("[RECENT CONVERSATION]\n" + "\n".join(history_lines))

        context_parts.append(f"User: {prompt}")
        enriched_prompt = "\n\n".join(context_parts)

        # Save the user message before calling the model
        iris_memory.save_message(db, session_id, "user", prompt, model=args.model)

        try:
            start_time = time.time()
            result = ask_ollama(enriched_prompt, args.host, args.model)
            duration_ms = int((time.time() - start_time) * 1000)
        except requests.RequestException as e:
            print(f"IRIS error: {e}", file=sys.stderr)
            db.close()
            sys.exit(1)

        # Save assistant response and run metadata
        assistant_msg_id = iris_memory.save_message(db, session_id, "assistant", result, model=args.model)
        iris_memory.save_model_run(db, session_id, assistant_msg_id, args.model, duration_ms)

        # Embed the assistant response in a background thread (non-blocking)
        embed_thread = threading.Thread(
            target=_embed_in_background,
            args=(assistant_msg_id, result, args.host),
            daemon=True,
        )
        embed_thread.start()
        embed_thread.join(timeout=8)

        db.close()
    elif args.mode == "research":
        query = " ".join(args.items).strip()
        results = tavily_research(query)
        print("\nIRIS Research Answer:\n")
        print(results.get("answer", ""))
        print("\nSources:\n")
        for result in results.get("results", []):
            print(f"- {result.get('title')}")
            print(f"  {result.get('url')}")

if __name__ == "__main__":
    main()
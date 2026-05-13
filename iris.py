#!/usr/bin/env python3

import argparse
import requests
import sys
import json
import os

DEFAULT_HOST = "http://10.10.10.2:11434"
DEFAULT_MODEL = "qwen2.5-coder:7b"

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

    for line in response.iter_lines():
        if line:
            chunk = line.decode("utf-8")
            try:
                data = json.loads(chunk)
                print(data.get("response", ""), end="", flush=True)
            except ValueError as e:
                # Handle JSON decoding error
                print(f"JSON parsing error: {e}", file=sys.stderr)

    print()
    return ""

def main():
    parser = argparse.ArgumentParser(description="IRIS CLI wrapper for Ollama")
    parser.add_argument("items", nargs="*", help="Prompt text and optional files")
    parser.add_argument("--host", default=DEFAULT_HOST, help="Ollama host URL")
    parser.add_argument("--model", default=DEFAULT_MODEL, help="Ollama model name")

    args = parser.parse_args()

    items = args.items
    prompt_parts = []

    for item in items:
        if os.path.isfile(item):
            with open(item, "r", encoding="utf-8") as f:
                file_content = f.read()
                prompt_parts.append(f"""
FILE: {item}
{file_content}
""")
        else:
            prompt_parts.append(item)

    prompt = " ".join(prompt_parts).strip()

    if not prompt:
        print("No prompt provided.", file=sys.stderr)
        sys.exit(1)

    try:
        result = ask_ollama(prompt, args.host, args.model)
        print(result)
    except requests.RequestException as e:
        print(f"IRIS error: {e}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
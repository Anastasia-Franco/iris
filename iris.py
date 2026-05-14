#!/usr/bin/env python3

import argparse
import requests
import sys
import json
import os
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
from dotenv import load_dotenv
from tavily import TavilyClient

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
    parser.add_argument("--mode", choices=["prompt", "research"], default="prompt", help="Specify the mode (default: prompt)")

    args = parser.parse_args()

    if args.mode == "prompt":
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
#!/usr/bin/env python3
"""Every tool call in the guide names a registered tool with real keywords, and the
reference page lists every tool and every Rhino command.

    cd rhino_mcp_server && uv run python ../scripts/dev/check_guide_calls.py
"""

from __future__ import annotations

import ast
import asyncio
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
GUIDE = ROOT / "docs" / "guide"
sys.path.insert(0, str(ROOT / "rhino_mcp_server" / "src"))

FENCE = re.compile(r"```python\n(.*?)```", re.S)
ENGLISH_NAME = re.compile(r'EnglishName\s*=>\s*"([a-z]+)"')


def registered_tools() -> dict[str, set[str]]:
    import rhinomcp  # noqa: F401 - registers the tools
    from rhinomcp.server import mcp

    tools = asyncio.run(mcp.list_tools())
    return {t.name: set((t.inputSchema or {}).get("properties", {}).keys()) for t in tools}


def calls_in(code: str):
    # A fence may be several bare calls, an assignment, or a fragment; parse what parses.
    try:
        tree = ast.parse(code)
    except SyntaxError:
        return
    for node in ast.walk(tree):
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name):
            yield node.func.id, [k.arg for k in node.keywords if k.arg]


def main() -> int:
    tools = registered_tools()
    problems: list[str] = []
    for page in sorted(GUIDE.glob("*.md")):
        text = page.read_text()
        for fence in FENCE.finditer(text):
            for name, keywords in calls_in(fence.group(1)):
                if name not in tools:
                    problems.append(f"{page.name}: `{name}(...)` is not a registered tool")
                    continue
                for keyword in keywords:
                    if keyword not in tools[name]:
                        problems.append(f"{page.name}: `{name}(...)` has no parameter `{keyword}`")

    reference = (GUIDE / "11-reference.md").read_text()
    for name in sorted(tools):
        if f"`{name}`" not in reference:
            problems.append(f"11-reference.md: tool `{name}` missing")
    commands = set()
    for source in (ROOT / "rhino_mcp_plugin" / "Commands").glob("*.cs"):
        commands.update(ENGLISH_NAME.findall(source.read_text()))
    for name in sorted(commands):
        if f"`{name}`" not in reference:
            problems.append(f"11-reference.md: command `{name}` missing")

    for problem in problems:
        print(problem)
    print(f"{len(tools)} tools, {len(commands)} commands, {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())

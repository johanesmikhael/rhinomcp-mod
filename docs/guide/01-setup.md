# Setup

Two packages, one on each side of a socket on `127.0.0.1:1999`:

| package | where | installs with | what it is |
| --- | --- | --- | --- |
| `rhinomcp-mod` plugin | Rhino 8 | Package Manager | the `.rhp` that runs the commands and answers the socket |
| `rhinomcp-mod` server | Python | `uvx` (via `uv`) | the MCP server Claude talks to; forwards each tool call to the plugin |

Requires Rhino 8 with Grasshopper (the assembly mode loads Rhino's own `KangarooSolver.dll`).

## 1. Plugin

Rhino: `Tools > Package Manager`, search `rhinomcp-mod`, install. Restart Rhino.

The listener starts when the plugin loads. On the command line:

```text
mcpmodversion
```

prints the loaded version. `mcpmodstart` / `mcpmodstop` start and stop the listener by hand.

A locally built `.rhp` (`rhino_mcp_plugin/bin/<Configuration>/net7.0/rhinomcp-mod.rhp`) has the
same plugin id as the package; Rhino loads one or the other, not both.

## 2. uv

macOS:

```bash
brew install uv
```

Windows (PowerShell; if scripts are blocked, `Set-ExecutionPolicy RemoteSigned -Scope CurrentUser` first):

```powershell
powershell -c "irm https://astral.sh/uv/install.ps1 | iex"
```

`uv --version` and `uvx --version` both print a version. If not, reopen the terminal; check `PATH`.

## 3. Claude Desktop

`Settings > Developer > Edit Config` opens `claude_desktop_config.json`:

- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
- Windows: `%APPDATA%\Claude\claude_desktop_config.json`

Add the `rhino` entry inside `mcpServers`, keeping whatever else is there:

```json
{
  "mcpServers": {
    "rhino": {
      "command": "uvx",
      "args": ["rhinomcp-mod"]
    }
  }
}
```

Restart Claude Desktop. `uvx` resolves the latest published version on first start and caches it;
`uvx --refresh rhinomcp-mod` picks up a new release.

## 4. Claude Code

```bash
claude mcp add rhino -- uvx rhinomcp-mod          # this project
claude mcp add -s user rhino -- uvx rhinomcp-mod  # every project
claude mcp list
```

Or the same block as above in `.mcp.json` at the project root, or under `mcpServers` in
`~/.claude.json`.

## 5. From source

For a checkout of this repository, run the server from it under a second name and keep only
one of the two entries active at a time - both connect to the same port and every tool would
appear twice:

```json
{
  "mcpServers": {
    "rhino-dev": {
      "command": "uv",
      "args": ["--directory", "/absolute/path/to/rhinomcp_mod/rhino_mcp_server", "run", "rhinomcp-mod"]
    }
  }
}
```

Claude Desktop has no disable switch for an entry; remove it from `mcpServers` or park it under
any other key. Per chat, the tools menu can switch a server's tools off.

The plugin from source: build `rhino_mcp_plugin/rhinomcp.csproj`; on macOS the default build
copies the `.rhp` into `/Applications/Rhino 8.app/Contents/PlugIns/`. Rebuilding while Rhino
has it loaded corrupts the loaded image - quit Rhino first.

## 6. Start order

1. Rhino, with a document open. A bare launch has no active document and the plugin's
   handlers return "no active document" until one is opened.
2. Claude Desktop or Claude Code. The server connects on its first tool call.

## 7. Verify

From Claude:

```python
list_plugins()                                # rhinomcp-mod listed, loaded
get_document_info(detail="inventory")         # objects, layers, units of the open document
get_rhino_log(lines=10)                       # what the plugin printed
```

From Rhino: `mcpmodversion`.

## 8. If it does not connect

| symptom | check |
| --- | --- |
| no Rhino tools in Claude | Rhino open with a document; `mcpmodversion` answers; `uvx --version` works; the JSON parses; Claude restarted after the edit |
| `Could not connect to Rhino` | listener not running - `mcpmodstart`; another process on 1999 |
| `Object reference not set` on every call | no active document - open a file |
| a tool call returns success and nothing happens, later calls hang | a prompting command was run through `run_rhino_command` and is waiting at its prompt - press Esc in Rhino; use the scripted form (`-mcpmodclearcache`) or pass the option tokens (`mcpmodstabilitydisplay Off`) |
| `rhinomcp-mod` not in Package Manager | exact name; internet; update Rhino |
| tools appear twice | two server entries active (`rhino` and `rhino-dev`) - keep one |

## 9. Session tools

| task | mcp | rhino command |
| --- | --- | --- |
| open a file | `open_file(path, close_current=True)` | `Open` |
| close the document | `close_file(save_changes=False)` | `Close` |
| list loaded plugins | `list_plugins()` | `PlugInManager` |
| run a Rhino command | `run_rhino_command("Zoom Extents")` | the command |
| read the command line | `get_rhino_log(lines=20)` | - |
| listener | - | `mcpmodstart`, `mcpmodstop`, `mcpmodversion` |

`run_rhino_command` passes the whole string to Rhino's script runner with a leading `_`, so
option tokens after the name answer the command's prompts. A command left waiting at a prompt
swallows the next calls; dashed (`-mcpmodclearcache`) and option forms (`mcpmodobb Off`) run
without prompting.

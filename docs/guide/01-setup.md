# Setup

rhinomcp-mod uses two packages connected through a socket on `127.0.0.1:1999`:

| package | where | installs with | description |
| --- | --- | --- | --- |
| `rhinomcp-mod` plugin | Rhino 8 | Package Manager | the `.rhp` that runs the commands and answers the socket |
| `rhinomcp-mod` server | Python | `uvx` (via `uv`) | receives MCP tool calls and forwards them to the plugin |

Rhino 8 with Grasshopper is required. Assembly mode loads Rhino's own `KangarooSolver.dll`.

## 1. Plugin

Rhino: `Tools > Package Manager`, search `rhinomcp-mod`, install. Restart Rhino.

The listener starts when the plugin loads. On the command line:

```text
mcpmodversion
```

prints the loaded version. `mcpmodstart` / `mcpmodstop` start and stop the listener by hand.

A locally built `.rhp` (`rhino_mcp_plugin/bin/<Configuration>/net7.0/rhinomcp-mod.rhp`) uses the
same plugin id as the packaged version. Rhino loads only one of them.

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
      "args": ["--with", "mcp[cli]<2", "rhinomcp-mod"]
    }
  }
}
```

The `mcp[cli]<2` pin is required through 0.4.0. `mcp` 2.x renamed `FastMCP` to `MCPServer` and
removed `mcp.server.fastmcp`, which the server imports; without the pin `uvx` resolves 2.x and the
server exits at import.

Restart Claude Desktop. `uvx` resolves the latest published version on first start and caches it;
`uvx --refresh rhinomcp-mod` picks up a new release.

## 4. Claude Code

```bash
claude mcp add rhino -- uvx --with 'mcp[cli]<2' rhinomcp-mod          # this project
claude mcp add -s user rhino -- uvx --with 'mcp[cli]<2' rhinomcp-mod  # every project
claude mcp list
```

Or the same block as above in `.mcp.json` at the project root, or under `mcpServers` in
`~/.claude.json`.

## 5. OpenCode

The OpenCode CLI and desktop app use the same global configuration:

- macOS and Linux: `~/.config/opencode/opencode.json` or
  `~/.config/opencode/opencode.jsonc`
- Windows: `%USERPROFILE%\.config\opencode\opencode.json` or
  `%USERPROFILE%\.config\opencode\opencode.jsonc`

Add the `rhino` entry under `mcp`, keeping whatever else is there:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "rhino": {
      "type": "local",
      "command": ["uvx", "--with", "mcp[cli]<2", "rhinomcp-mod"]
    }
  }
}
```

The OpenCode format uses `mcp` rather than `mcpServers`, requires `type`, and combines the
executable and its arguments in one `command` array.

Restart OpenCode after editing the file. The CLI and desktop app both load this global
configuration, so the `rhino` server is available in both. From the CLI, verify it with:

```bash
opencode mcp list
```

## 6. From source

For a checkout of this repository, run the server under a second name. Keep only one server
entry active at a time because both entries connect to the same port and would expose every
tool twice.

Claude Desktop and Claude Code:

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

OpenCode:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "rhino-dev": {
      "type": "local",
      "command": [
        "uv",
        "--directory",
        "/absolute/path/to/rhinomcp_mod/rhino_mcp_server",
        "run",
        "rhinomcp-mod"
      ]
    }
  }
}
```

Claude Desktop has no disable switch for an entry; remove the inactive entry from `mcpServers`.
In OpenCode, set `"enabled": false` on the inactive entry. Keep the published and source
entries under different names so their purpose remains clear.

The plugin from source: build `rhino_mcp_plugin/rhinomcp.csproj`; on macOS the default build
copies the `.rhp` into `/Applications/Rhino 8.app/Contents/PlugIns/`. Rebuilding while Rhino
has it loaded corrupts the loaded image - quit Rhino first.

## 7. Start order

1. Rhino, with a document open. A bare launch has no active document and the plugin's
   handlers return "no active document" until one is opened.
2. Start the MCP client: Claude Desktop, Claude Code, or OpenCode. The server connects on its
   first tool call.

## 8. Verify

From the MCP client:

```python
list_plugins()                                # rhinomcp-mod listed, loaded
get_document_info(detail="inventory")         # objects, layers, units of the open document
get_rhino_log(lines=10)                       # what the plugin printed
```

From Rhino: `mcpmodversion`.

## 9. If it does not connect

| symptom | check |
| --- | --- |
| no Rhino tools in the MCP client | Rhino open with a document; `mcpmodversion` answers; `uvx --version` works; the JSON parses; the client restarted after the edit; for OpenCode, `opencode mcp list` includes `rhino` |
| `ModuleNotFoundError: No module named 'mcp.server.fastmcp'` in the client's MCP log | `mcp` 2.x was resolved - add `--with 'mcp[cli]<2'` to the `uvx` invocation |
| `Could not connect to Rhino` | listener not running - `mcpmodstart`; another process on 1999 |
| `Object reference not set` on every call | no active document - open a file |
| a tool call returns success and nothing happens, later calls hang | a prompting command was run through `run_rhino_command` and is waiting at its prompt - press Esc in Rhino; use the scripted form (`-mcpmodclearcache`) or pass the option tokens (`mcpmodstabilitydisplay Off`) |
| `rhinomcp-mod` not in Package Manager | exact name; internet; update Rhino |
| tools appear twice | two server entries active (`rhino` and `rhino-dev`) - keep one |

## 10. Session tools

| task | mcp | rhino command |
| --- | --- | --- |
| open a file | `open_file(path, close_current=True)` | `Open` |
| close the document | `close_file(save_changes=False)` | `Close` |
| list loaded plugins | `list_plugins()` | `PlugInManager` |
| run a Rhino command | `run_rhino_command("Zoom Extents")` | the command |
| read the command line | `get_rhino_log(lines=20)` | - |
| listener | - | `mcpmodstart`, `mcpmodstop`, `mcpmodversion` |

Pass `close_current=True` when switching files. Rhino can hold several documents at once, but
every tool here works on the active one, and an open that leaves an extra document behind does
not reliably make the new file active - so the call is refused rather than left pointing at the
wrong model. Closing as you go leaves Rhino one document to activate, which is reliable. The
new file is opened before the old one closes, so Rhino is never left with no document at all,
and `open_file` returns `active_name` alongside `name` for the document actually in effect.
Closing discards unsaved changes unless `save_current=True`.

`run_rhino_command` passes the whole string to Rhino's script runner with a leading `_`, so
option tokens after the name answer the command's prompts. A command left waiting at a prompt
swallows the next calls; dashed (`-mcpmodclearcache`) and option forms (`mcpmodobb Off`) run
without prompting.

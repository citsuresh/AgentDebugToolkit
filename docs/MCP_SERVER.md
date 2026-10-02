# MCP Server — AgentDebugToolkit.Mcp

`AgentDebugToolkit.Mcp` (built as `agentdebug-mcp.exe`) is a thin local
[MCP](https://modelcontextprotocol.io/) (Model Context Protocol) server, built on the official
`ModelContextProtocol` .NET SDK with stdio transport, that exposes the existing
`agentdebug-vs.exe` and `agentdebug-ui.exe` CLIs as MCP tools so an MCP client (Claude Desktop,
Cowork, etc.) can call this toolkit directly instead of a human/agent shelling out to each CLI.

**It is a thin wrapper only.** It contains no debugger, COM, or UIA logic of its own: every tool
builds an argument list for the matching CLI verb (via `ProcessStartInfo.ArgumentList`, never a
shell/concatenated command string), runs it as a child process, and relays the CLI's own JSON
output back unchanged. See `docs/CLI_CONTRACT.md` for what each underlying verb actually does —
this document only covers how the MCP layer maps onto that contract.

## How a tool call works

1. The MCP client calls a tool, e.g. `vs_debugger_status` or `ui_click`.
2. The server builds the corresponding CLI invocation (e.g. `agentdebug-vs.exe debugger-status`)
   using the tool's typed parameters, with `DOTNET_ROOT`/`DOTNET_ROOT_X64`/`DOTNET_ROOT_X86`
   stripped from the child process environment (the toolkit's known DOTNET_ROOT gotcha — see
   README.md).
3. The child process's stdout is parsed as the CLI's one JSON object and returned to the client
   unchanged, under `Result`, alongside the process `ExitCode`. This includes the CLI's own
   `"success": false` error envelopes (e.g. `com-busy-retry-exhausted`, `frame-selection-stale`,
   `frame-selection-failed`, `frame-not-managed`, `no-frame-selected`, `ambiguous-process`,
   `process-not-found`) — these are **not** treated as MCP/wrapper failures, so a caller can
   branch on the CLI's own `error` code exactly as if it had invoked the CLI directly.
4. If the CLI could not be run or did not produce valid JSON (missing exe, process failed to
   start, timed out, non-JSON output), the tool instead returns a **wrapper-level** error via
   `WrapperErrorCode`/`WrapperErrorMessage` (and `RawOutput` for diagnostics), clearly
   distinguishable from the CLI's own error codes.
5. All server logging/diagnostics go to **stderr only** — stdout belongs exclusively to the MCP
   protocol stream, and any stray stdout write would corrupt it.

Every call is bounded by a timeout: the child process is killed if it does not complete in time,
and the timeout error is reported distinctly (`WrapperErrorCode: "timeout"`). Long-running verbs
(`vs_wait_for_break`, `ui_wait_for_window_change`, `ui_wait_for_element`,
`ui_wait_for_process_responding`) accept a `timeoutMs` parameter that is capped at a sane maximum
(120 seconds) regardless of the value requested.

## Locating the CLI exes

`agentdebug-vs.exe` and `agentdebug-ui.exe` are built into different per-project output folders,
so the server resolves each one's bin directory independently, and never searches the disk or
guesses another location ("ask, don't guess"):

1. An exe-specific environment variable, if set: `AGENTDEBUG_VS_BIN` for `agentdebug-vs.exe`,
   `AGENTDEBUG_UI_BIN` for `agentdebug-ui.exe`.
2. Otherwise, the shared `AGENTDEBUG_TOOLKIT_BIN` environment variable, if set — use this only
   when both exes happen to live in (or have been copied/published into) the same folder.
3. Otherwise, the MCP server's own build output directory (`AppContext.BaseDirectory`) — this
   only works if a CLI exe happens to sit next to `agentdebug-mcp.exe`, which is not the case for
   a normal multi-project build; setting `AGENTDEBUG_VS_BIN`/`AGENTDEBUG_UI_BIN` explicitly is
   recommended.

If an exe is not found, the tool call returns a wrapper-level `exe-not-found` error naming every
path that was checked (the specific env var's path if set, the shared env var's path if set, and
the server build output fallback), so you can see exactly what was tried.

## Safety split

**Annotation criteria:**
- `readOnlyHint: true` — no state change anywhere (not the debuggee, not the debugger session,
  not disk).
- `destructiveHint: true` — changes the live debuggee, ends/starts a debug session, or otherwise
  can't be undone by a single follow-up call. **Documented exception:** the normal
  execution-control loop (`vs_continue`, `vs_step_over`/`vs_step_into`/`vs_step_out`) resumes the
  debuggee but is kept `destructiveHint: false` by decision — see below.
- Selection-only tools (`vs_select_thread`, `vs_select_frame`) are `readOnlyHint: false` (they do
  change debugger state) but `destructiveHint: false` (the change is just which thread/frame
  subsequent calls target, trivially reversible by selecting again).

Side-effecting operations are kept as **separate tools** from their read-only counterparts, so an
MCP client can grant/prompt approval per tool rather than per call:

- `vs_evaluate` always uses the safe, non-mutating expression-read path
  (`Debugger.GetExpression`) and **never** passes `--allowSideEffects` — this is hardcoded, not a
  caller-controllable parameter, so a client cannot opt into side effects through this tool.
- `vs_execute_statement` is the **only** tool that passes `--allowSideEffects true`
  (`Debugger.ExecuteStatement`), which can run property getters, method calls, and assignment
  statements against the live debuggee. Its description explicitly calls out this risk.
- Every tool's `readOnlyHint`/`destructiveHint` MCP annotations are set to reflect its real
  effect:
  - **Read-only** (`readOnlyHint: true`): `vs_debugger_status`, `vs_get_callstack`,
    `vs_get_locals`, `vs_get_exception_info`, `vs_list_breakpoints`, `vs_list_threads`,
    `vs_evaluate`, `vs_wait_for_break`, `ui_list_windows`, `ui_inspect`, `ui_get_cursor_pos`,
    `ui_get_text`, `ui_wait_for_window_change`, `ui_wait_for_element`,
    `ui_wait_for_process_responding`, `ui_delay`, `ui_read_visible_text`,
    `ui_find_first`, `ui_find_all`.
  - `ui_screenshot` is **not** read-only (`readOnlyHint: false`): although it does not change
    target application state, it writes a new PNG file to disk, which is a side effect.
  - **Destructive** (`destructiveHint: true`): `vs_start_debugging` (launches a new debuggee
    process), `vs_stop_debugging` (terminates the debuggee, unlike `vs_detach`),
    `vs_set_breakpoint`/`vs_remove_breakpoint` (change debugger state), `vs_attach_process`
    (attaches the debugger to a live process), `vs_break_all` (forces every thread in the
    debuggee to stop), `vs_detach` (ends the debug session's monitoring of the process, even
    though the process itself keeps running), `vs_execute_statement` (unsafe/side-effecting
    evaluation), and every state-changing `agentdebug-ui` verb that acts on the target
    application (`ui_click`, `ui_right_click`, `ui_double_click`, `ui_drag`, `ui_type`,
    `ui_set_grid_cell`, `ui_activate`, `ui_send_keys`, `ui_submit_chat_message`).
  - Debugger **selection-only** tools (`vs_select_thread`, `vs_select_frame`, `ui_attach`,
    `ui_set_context`, `ui_move_mouse`) are marked `readOnlyHint: false` but `destructiveHint: false`
    — they only change which thread/frame/target subsequent calls use, not in a way that destroys
    data or terminates a process.
  - **Execution-control** tools (`vs_continue`, `vs_step_over`/`vs_step_into`/`vs_step_out`) are
    also marked `readOnlyHint: false` but `destructiveHint: false`, **by decision, not because they
    lack effect** — they do resume the debuggee. They are the normal debugging loop, and requiring
    per-step approval would be impractical. Clients that want tighter control should require
    approval for these tools explicitly rather than relying on `destructiveHint`.

## Tool list

### Visual Studio debugger tools (`VsDebuggerTools`, wrapping `agentdebug-vs.exe`)

| Tool | CLI verb | Notes |
| --- | --- | --- |
| `vs_debugger_status` | `debugger-status` | Read-only |
| `vs_get_callstack` | `get-callstack` | Read-only |
| `vs_get_locals` | `get-locals` | Read-only |
| `vs_get_exception_info` | `get-exception-info` | Read-only |
| `vs_continue` | `continue` | |
| `vs_step_over` | `step-over` | |
| `vs_step_into` | `step-into` | |
| `vs_step_out` | `step-out` | |
| `vs_start_debugging` | `start-debugging` | Destructive (launches new process) |
| `vs_stop_debugging` | `stop-debugging` | Destructive (terminates debuggee) |
| `vs_set_breakpoint` | `set-breakpoint` | Destructive (changes debugger state) |
| `vs_list_breakpoints` | `list-breakpoints` | Read-only |
| `vs_remove_breakpoint` | `remove-breakpoint` | Destructive (changes debugger state) |
| `vs_wait_for_break` | `wait-for-break` | Read-only; `timeoutMs` capped at 120000 |
| `vs_attach_process` | `attach-process` | Destructive (attaches debugger to a live process) |
| `vs_break_all` | `break-all` | Destructive (forces every thread in the debuggee to stop) |
| `vs_detach` | `detach` | Destructive (ends debug session monitoring); debuggee keeps running afterward |
| `vs_list_threads` | `list-threads` | Read-only |
| `vs_select_thread` | `select-thread` | |
| `vs_select_frame` | `select-frame` | Selection only valid until next continue/step/break |
| `vs_evaluate` | `evaluate` | Read-only; never passes `--allowSideEffects` |
| `vs_execute_statement` | `evaluate --allowSideEffects true` | **Unsafe** — the only tool that can run side-effecting evaluation |

### UI automation tools (`UiAutomationTools`, wrapping `agentdebug-ui.exe`)

| Tool | CLI verb | Notes |
| --- | --- | --- |
| `ui_attach` | `attach` | |
| `ui_list_windows` | `list-windows` | Read-only |
| `ui_inspect` | `inspect` | Read-only |
| `ui_click` | `click` | Destructive |
| `ui_right_click` | `right-click` | Destructive |
| `ui_double_click` | `double-click` | Destructive |
| `ui_drag` | `drag` | Destructive |
| `ui_move_mouse` | `move-mouse` | |
| `ui_get_cursor_pos` | `get-cursor-pos` | Read-only |
| `ui_type` | `type` | Destructive |
| `ui_get_text` | `get-text` | Read-only |
| `ui_set_grid_cell` | `set-grid-cell` | Destructive |
| `ui_wait_for_window_change` | `wait-for-window-change` | Read-only; `timeoutMs` capped at 120000 |
| `ui_wait_for_element` | `wait-for-element` | Read-only; `timeoutMs` capped at 120000 |
| `ui_wait_for_process_responding` | `wait-for-process-responding` | Read-only; `timeoutMs` capped at 120000 |
| `ui_delay` | `delay` | |
| `ui_set_context` | `set-context` | |
| `ui_read_visible_text` | `read-visible-text` | Read-only |
| `ui_screenshot` | `screenshot` | Writes a PNG file — not read-only |
| `ui_activate` | `activate` | Destructive (steals foreground focus) |
| `ui_send_keys` | `send-keys` | Destructive |
| `ui_submit_chat_message` | `submit-chat-message` | Destructive; treat as high-impact (real submission) |
| `ui_find_first` | `find-first` | Read-only |
| `ui_find_all` | `find-all` | Read-only |

No generic "run any verb"/"run any command" tool is exposed — every tool corresponds to exactly
one documented CLI verb with typed, described parameters matching `docs/CLI_CONTRACT.md`.

`agentdebug-console.exe`'s verbs are not yet wrapped as MCP tools (out of scope for this initial
server; can be added the same way if/when needed).

## Configuration: `AGENTDEBUG_VS_BIN` / `AGENTDEBUG_UI_BIN` / `AGENTDEBUG_TOOLKIT_BIN`

For a local dev build of this repo, `agentdebug-vs.exe` and `agentdebug-ui.exe` live in separate
per-project `bin\Debug\net8.0-windows` folders, so set the two exe-specific variables before
launching `agentdebug-mcp.exe`:

- `AGENTDEBUG_VS_BIN` — folder containing `agentdebug-vs.exe`
  (`src\AgentDebugToolkit.Debugger.VisualStudio\bin\Debug\net8.0-windows`).
- `AGENTDEBUG_UI_BIN` — folder containing `agentdebug-ui.exe`
  (`src\AgentDebugToolkit.UiAutomation.Cli\bin\Debug\net8.0-windows`).

If you've copied or published both exes into one shared folder (e.g. a release layout), you can
set `AGENTDEBUG_TOOLKIT_BIN` instead and omit the two exe-specific variables — each exe falls
back to it when its own specific variable isn't set.

## Sample Claude Desktop configuration

This is a **sample only** — review and adapt the paths before adding it to your own
`%APPDATA%\Claude\claude_desktop_config.json`. This repository does not modify that file.

```json
{
  "mcpServers": {
    "agentdebug-toolkit": {
      "command": "C:\\MyFiles\\Git\\AgentDebugToolkit\\src\\AgentDebugToolkit.Mcp\\bin\\Debug\\net8.0-windows\\agentdebug-mcp.exe",
      "args": [],
      "env": {
        "AGENTDEBUG_VS_BIN": "C:\\MyFiles\\Git\\AgentDebugToolkit\\src\\AgentDebugToolkit.Debugger.VisualStudio\\bin\\Debug\\net8.0-windows",
        "AGENTDEBUG_UI_BIN": "C:\\MyFiles\\Git\\AgentDebugToolkit\\src\\AgentDebugToolkit.UiAutomation.Cli\\bin\\Debug\\net8.0-windows"
      }
    }
  }
}
```

## Verification


`tools/Test-McpServer.ps1` starts the server over stdio, performs the MCP `initialize` handshake,
calls `tools/list`, and calls the read-only `vs_debugger_status` tool against a running Visual
Studio instance, printing the real JSON-RPC responses. Run it after any change to this project to
confirm the server still speaks MCP correctly end-to-end:

```powershell
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
.\tools\Test-McpServer.ps1
```

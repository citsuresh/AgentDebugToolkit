# CLAUDE.md

Guidance for Claude when using this repo's MCP servers. Details: `docs/MCP_SERVER.md`,
`docs/CLI_CONTRACT.md`.

## MCP servers

Both entries run `agentdebug-mcp.exe` (see "Sample Claude Desktop configuration" in
`docs/MCP_SERVER.md`):

- `agentdebug-toolkit` (default `--tools vs,ui`): `vs_*` tools wrap `agentdebug-vs.exe`
  (Visual Studio debugger); `ui_*` tools wrap `agentdebug-ui.exe` (UI automation).
- `agentdebug-console` (`--tools console`): the `console_*` tools wrap
  `agentdebug-console.exe` (console session automation).

## Rules

- Use sub-agents for ALL calls to agentdebug MCP tools, so tool output does not fill the
  main chat. Have the sub-agent return only the facts needed.
- Use read-only tools by default (`readOnlyHint: true`; listed in `docs/MCP_SERVER.md`).
  Ask before calling any state-changing tool. This includes tools marked
  `destructiveHint: false` that still change state, such as `vs_continue`, `vs_step_*`,
  `vs_select_*` and `ui_move_mouse`.
- Never work around a block: a refused call, an allowlist or blocklist rejection, or a
  missing permission. Report it and stop.
- Never click or approve permission prompts on the user's behalf; the user approves those
  themselves.
- Redact secrets, tokens and personal data from anything you report back (Itron policy).
- `console_launch` is gated in the MCP server by `AGENTDEBUG_CONSOLE_ALLOWED_EXE`: a
  `;`-separated list of full executable paths. Never allowlist an interpreter, shell or
  runner (`python.exe`, `node.exe`, `dotnet.exe`, `powershell.exe`, `cmd.exe` and similar):
  their arguments can run arbitrary code.
- Rebuilding the MCP server needs Claude Desktop fully closed. The MSIX install keeps
  `agentdebug-mcp.exe` running and locks its output. After a rebuild, tool schemas in a
  long-running chat can be stale, so verify in a fresh chat.

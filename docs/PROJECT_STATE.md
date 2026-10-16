# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-16: Cross-process window discovery for `agentdebug-ui` (`list-windows` /
`wait-for-window-change`: `--all`, `--processName`, title/class filters, default exclusions,
`--limit`, `--until match`, `--excludeHwnds`) and the matching MCP tool parameters. Uncommitted,
pending review.

2026-10-14: Fixed MCP `console_launch` timing out after 30000 ms even though the session was
created. Root cause:
inherited the CLI's own standard handles (the MCP runner's pipe ends). `CliRunner`'s
`WaitForExitAsync` waits for stream EOF, so it waited on the long-lived broker. Pre-existing
since `f918009`; unrelated to the `9e37bb2` parser change.

Fix `4f1a3c4`: `ConsoleVerbs.ClearStandardHandleInheritance()` clears `HANDLE_FLAG_INHERIT`
on the CLI's std handles right before `Process.Start` (best effort; P/Invokes in
`NativeConPty`). The broker's private stdio redirection is unchanged. Full solution build
passed (0 warnings, 0 errors). Disposable PS 5.1 check: launch returned `success:true` in
~3.3 s, wait-for-text/is-running/stop worked, no leftover broker. The user confirmed the MCP
`console_launch` positive-path test passes through Claude Desktop.

The temporary `ping.exe` allowlist in the Claude Desktop config was reverted to its original
placeholder (outside the repo; backups kept beside the config).

Housekeeping: added root `CLAUDE.md` (`8120921`, pushed) with MCP server guidance and rules for
Claude sessions (sub-agents for agentdebug MCP calls, read-only by default, no workarounds or
prompt approvals, redaction, console allowlist, rebuild with Claude Desktop closed). The
`vs-debug` skill was bumped to v6 in the Skills repo to document VS strict option parsing
(`--expression=--i`); the user committed it. No skill named `agentdebug-mcp-subagent-workflow`
exists anywhere.

## Open Tasks / Known Issues

The installed `~/.copilot/skills/vs-debug` copy is still v5 until `Install-Skills.ps1` is run.
The stale-schema-after-rebuild note in `CLAUDE.md` is user-reported, not verified.

No project tests are registered in Test Explorer. Live VS evaluation and broker transport
for the strict-parser change (`9e37bb2`) remain untested beyond disposable checks.

Cross-process window discovery (2026-10-16): MCP tools not yet exercised from a fresh Claude
Desktop chat. `tools/Test-McpServer.ps1` was deliberately not run (it prints raw window titles).

The `isModal` field still treats ownership as modal state, so owned modeless windows are
misclassified; deferred to future multi-window/dialog-awareness work. Graph caller queries
can fail with `An item with the same key has already been added. Key: Program`; fall back to
text search.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs`, `UiaHelper.cs`, `NativeMethods.cs`,
  `src/AgentDebugToolkit.Core/Models/WindowAndElementInfo.cs` -- cross-process window discovery
  (`--all`, `--processName`, filters, `--limit`, `--until match`, `--excludeHwnds`,
  `WindowInfo.processName`).
- `src/AgentDebugToolkit.Mcp/UiAutomationTools.cs` -- matching `ui_list_windows` /
  `ui_wait_for_window_change` parameters.
- `docs/CLI_CONTRACT.md`, `docs/MCP_SERVER.md`, `docs/KNOWN_OPEN_FINDINGS.md`,
  `docs/DESIGN_DECISIONS.md` -- cross-process window discovery.
- `tools/Watch-CopilotChat.ps1` -- synced from the skills repo (`7788b24`).
- `CLAUDE.md` -- new: MCP server guidance and rules for Claude sessions.
- `src/AgentDebugToolkit.ConsoleAutomation.Cli/ConsoleVerbs.cs` -- std-handle inheritance
  cleared before starting the broker.
- `src/AgentDebugToolkit.ConsoleAutomation.Cli/NativeConPty.cs` -- `GetStdHandle` /
  `SetHandleInformation` P/Invokes.
- `docs/CLI_CONTRACT.md` -- broker has private stdio pipes; launch returns promptly when
  output is captured.
- `docs/KNOWN_OPEN_FINDINGS.md` -- RESOLVED console launch inherited-handle timeout.
- `docs/DESIGN_DECISIONS.md` -- broker must not inherit the caller's standard handles.
- `docs/KEY_FLOWS.md` -- parser and shared argument-formatting flows.
- `docs/PROJECT_STATE.md` -- session snapshot.
- `docs/full-graph.agentdebugtoolkit.json`, `docs/project-dependencies.agentdebugtoolkit.json`
  -- incremental graph refresh.

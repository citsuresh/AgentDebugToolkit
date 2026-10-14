# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-14: Fixed MCP `console_launch` timing out after 30000 ms even though the session was
created. Root cause: `StartBroker` uses `Process.Start` with redirected streams, so the broker
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

## Open Tasks / Known Issues

No project tests are registered in Test Explorer. Live VS evaluation and broker transport
for the strict-parser change (`9e37bb2`) remain untested beyond disposable checks.

The `isModal` field still treats ownership as modal state, so owned modeless windows are
misclassified; deferred to future multi-window/dialog-awareness work. Graph caller queries
can fail with `An item with the same key has already been added. Key: Program`; fall back to
text search.

## Recently Changed Files

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

# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-31: Replaced the stale chat watcher with the authoritative V1 script; made MCP `ui_inspect`
read-only by omitting its screenshot option; improved `list-windows` argument diagnostics; and tagged
the real-click UI test for interactive-desktop filtering. Changes remain unstaged and uncommitted.

## Verification and Diagnostic Evidence

- `AgentDebugToolkit.UiAutomation.Cli` native Visual Studio build succeeded.
- `git diff --check` passed. No changes are staged.
- Tests: building the test project in place stops at the locked `agentdebug-mcp.exe` (running MCP
  servers hold the default output). Run from a temporary copy of the working tree instead (robocopy
  without `bin`, `obj`, `.git`), where `dotnet test` with `Category!=InteractiveDesktop` passed 36 of
  36 on 2026-10-31. The interactive-desktop test was excluded by the filter and was not run.
- Phase reviews found no scoped source issues. Final review flagged the copied V1 header's reference
  to a V2 script absent from this repository; the source was retained verbatim as requested. The
  code graph returned symbol-not-found for queried MCP and list-windows symbols; direct source
  searches were used as fallback.

## Open Tasks / Known Issues

- Run the interactive-desktop test (the real-click test) once on an interactive desktop, and confirm
  that the filter `Category!=InteractiveDesktop` excludes exactly that test.
- `set-grid-cell` parity is not consistently demonstrated: intermittent row-resolution failures
  occurred on both builds. Diagnose internal termination and provider/layout timing without
  inferring root cause from snapshots alone.
- Cursor-session concurrent-start ownership race matches the external script and remains a
  documented limitation by user decision; cross-process concurrency and real cursor drawing
  remain unverified.
- Prior unresolved items, not revalidated: installed vs-debug skill update, fresh-chat MCP schema
  checks, cross-process discovery MCP validation, strict-parser live VS/broker checks, and owned
  modeless windows misclassified by `isModal`. `tools\Test-McpServer.ps1` was not run or modified.
- Graph queries previously returned symbol-not-found for ScrollSearch/ResolveGridCell; prior
  caller queries reported duplicate key `Program`. Do not assume those query limitations are resolved.

## Recently Changed Files

- `tools\Watch-CopilotChat.ps1` (renamed to `Watch-CopilotChatV1.ps1`) and `docs\CLI_CONTRACT.md`.
- `src\AgentDebugToolkit.Mcp\UiAutomationTools.cs`, the UI test project, and
  `docs\MCP_SERVER.md` for the read-only inspect contract.
- `src\AgentDebugToolkit.UiAutomation.Cli\Program.cs`, `ListWindowsArgumentTests.cs`, and
  `docs\CLI_CONTRACT.md` for `list-windows` diagnostics.
- `ControlTypeSelectorTests.cs` and `docs\TESTING.md` for interactive-desktop test filtering.

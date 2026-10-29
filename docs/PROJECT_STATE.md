# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-29: Adding cursor-session CLI/MCP support with injectable cursor operations and a shared
flag compatible with `AgentCursor.ps1`. Changes are uncommitted; validation uses a separate
artifacts path. Earlier intermittent `set-grid-cell` R0075 row-resolution failures remain open.

## Verification and Diagnostic Evidence

- Cursor-session isolated full solution build after final changes: 6 NU1900 warnings, 0 errors;
  UI tests: 34 passed, 0 failed; `git diff --check` passed.
- R0075/R0003/R9999 comparison against `d7bec54`: the complete rerun matched, including edited
  value readback and `element-not-found` for R9999, but an initial baseline R0075 failure occurred.
- Five reset-controlled alternating R0075 pairs initially passed 5/5 on both builds. On 2026-10-22,
  a fresh harness gave baseline 2/5, current 5/5; the next rerun gave baseline 5/5, current 4/5.
- Temp-only provider tracing, with an instrumented ScrollViewer peer/template: baseline 4/5,
  current 3/5. Every success passed readback after 62 scroll calls. Failures: current pair 1
  (second), 7 calls; current pair 4 (first), 43 calls; baseline pair 4 (second), 56 calls.
  R0075 was absent at those failures; all returned "No grid cell was found for row selector
  [Name] 'R0075' and column 1." The traces are provider snapshots, not private CLI lookup/branch
  traces, and instrumentation may affect timing. Exact internal termination cause is unconfirmed.
- External baseline worktrees removed. Diagnostic source/logs removed; both GridTrace harness
  executables were subsequently deleted and verified absent. No product code changed during
  parity diagnostics; no process was forcibly stopped during those runs; gap-check app untouched.

## Open Tasks / Known Issues

- Do not call set-grid-cell parity consistently demonstrated: intermittent row resolution
  failures occurred on both builds. Diagnose internal termination and provider/layout timing
  without inferring root cause from snapshots alone.
- Cursor-session concurrent-start ownership race matches the external script and remains a
  documented limitation by user decision; cross-process concurrency and real cursor drawing
  remain unverified.
- `scripts\agent\Rebuild-Mcp.ps1` closes Claude and rebuilds MCP only; it does not rebuild the UI
  CLI. Rebuild the solution or UI project separately when UI source changes, and verify timestamps.
- Prior unresolved items, not revalidated: installed vs-debug skill update, fresh-chat MCP schema
  checks, cross-process discovery MCP validation, strict-parser live VS/broker checks, and owned
  modeless windows misclassified by `isModal`. `tools\Test-McpServer.ps1` was not run or modified.
- Graph queries previously returned symbol-not-found for ScrollSearch/ResolveGridCell; prior
  caller queries reported duplicate key `Program`. Graph is refreshed during Bootstrap/End Session;
  do not assume those query limitations are resolved without checking.

## Recently Changed Files

- `src\AgentDebugToolkit.UiAutomation.Cli\Program.cs`, `CursorSession.cs` -- new cursor CLI and Win32 port.
- `src\AgentDebugToolkit.Mcp\UiAutomationTools.cs` -- start/stop MCP tools.
- `tests\AgentDebugToolkit.UiAutomation.Tests\CursorSessionTests.cs` -- isolated cursor session tests.
- `docs\CLI_CONTRACT.md`, `docs\MCP_SERVER.md`, `docs\DESIGN_DECISIONS.md`, `docs\CODE_SUMMARY.md`,
  `docs\KEY_FLOWS.md`, `docs\PROJECT_STATE.md` -- feature contract and memory updates.

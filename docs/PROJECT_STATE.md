# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-22: Investigating intermittent `set-grid-cell` R0075 row/cell resolution failures.
Use pre-fix baseline `d7bec54`; `968cfe8` already includes the `isSatisfied` change and is not
an appropriate pre-fix baseline. Product HEAD is `968cfe8`; scroll-search fix `f4c467f` and
contract/decision docs `968cfe8` were committed and pushed during this session.

`scroll-into-view` searches until fully visible with an optional satisfaction predicate,
tolerates three consecutive non-advancing steps, and retains the latest rejected non-null
partial result. Timeout remains timeout. `set-grid-cell` supplies no predicate, so its original
single non-advancing-step termination remains unchanged. The verb does not request activation
and makes no cursor or input calls, but the target's UI Automation provider may activate its
window during a scroll.

## Verification and Diagnostic Evidence

- Full solution build on 2026-10-19: 0 warnings, 0 errors; UI CLI and MCP executable timestamps
  were later than the final source edit. No UiaHelper/ScrollSearch unit tests or test projects found.
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
- `scripts\agent\Rebuild-Mcp.ps1` closes Claude and rebuilds MCP only; it does not rebuild the UI
  CLI. Rebuild the solution or UI project separately when UI source changes, and verify timestamps.
- Prior unresolved items, not revalidated: installed vs-debug skill update, fresh-chat MCP schema
  checks, cross-process discovery MCP validation, strict-parser live VS/broker checks, and owned
  modeless windows misclassified by `isModal`. `tools\Test-McpServer.ps1` was not run or modified.
- Graph queries previously returned symbol-not-found for ScrollSearch/ResolveGridCell; prior
  caller queries reported duplicate key `Program`. Graph is refreshed during Bootstrap/End Session;
  do not assume those query limitations are resolved without checking.

## Recently Changed Files

- `src\AgentDebugToolkit.UiAutomation.Cli\UiaHelper.cs`, `Program.cs` -- scroll-search fix (`f4c467f`).
- `scripts\agent\Rebuild-Mcp.ps1`, `docs\SCRIPTS.md` -- reusable MCP rebuild script (`f4c467f`).
- `docs\CLI_CONTRACT.md`, `docs\DESIGN_DECISIONS.md` -- scrolling behavior docs (`968cfe8` and earlier entries).
- `.github\copilot-instructions.md` -- memory skill Bootstrap refresh to v23 (uncommitted).
- `docs\PROJECT_STATE.md` -- current session snapshot (uncommitted).
- `docs\full-graph.agentdebugtoolkit.json`, `docs\project-dependencies.agentdebugtoolkit.json`
  -- Bootstrap full graph refresh, followed by End Session incremental update (uncommitted).

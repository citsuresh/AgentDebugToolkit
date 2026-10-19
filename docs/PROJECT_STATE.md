# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-16: Added `scroll-into-view` and MCP `ui_scroll_into_view`. UIA-only scrolling uses
scroll/item patterns, leaves the target's scroll position changed, and does not activate the
foreground window or move the cursor. The approved short re-query retry was added for a virtualized
item that disappears after scrolling. Whole-solution build passed (0 warnings, 0 errors).

## Open Tasks / Known Issues

- A 56-target WPF scroll-search comparison found 56/56 with `large` and 48/56 with `small`;
  one focused small-step target still missed at max scrolls. See `docs/KNOWN_OPEN_FINDINGS.md`.
- Wheel fallback, horizontal virtualization, and searching from the current position remain open.
- Set-grid-cell baseline/new small-grid outputs matched for far, near, and nonexistent rows; the
  nonexistent-row case returned `element-not-found` in both and both test windows remained alive.
  Cell-value readback was not verified by the harness.
- MCP live verification is deferred to the user. `tools/Test-McpServer.ps1` was not run because it
  prints raw window titles. No repository test project is registered.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs`, `UiaHelper.cs` -- new `scroll-into-view`
  verb and shared vertical scroll-search; approved post-scroll selector retry.
- `src/AgentDebugToolkit.Mcp/UiAutomationTools.cs` -- new `ui_scroll_into_view` tool.
- `docs/CLI_CONTRACT.md`, `docs/MCP_SERVER.md`, `docs/DESIGN_DECISIONS.md`,
  `docs/KNOWN_OPEN_FINDINGS.md` -- scroll behavior, MCP guidance, design decision, observed
  WPF results, and remaining limitations.
- `docs/CODE_SUMMARY.md`, `docs/KEY_FLOWS.md`, `docs/PROJECT_STATE.md` -- helper responsibility,
  scroll call flow, and session snapshot.

# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-11-08: Completed Phase 1 item 1 and its requested review fixes — absolute-coordinate targets
for click, right-click, and double-click. Phase 1 items 2-5 remain deferred.

## Bounds / Restoration Decisions

- Coordinate input activates the target first, restoring it if minimized, then checks a freshly
  read `GetWindowRect` immediately before input. Selector behavior is unchanged.
- After requesting restoration, poll every 20 ms for up to 1 second until the window is no longer
  minimized and its rectangle matches on two consecutive successful reads. Timeout reports
  `foreground-activation-failed` with a restore-timeout message and sends no coordinate input.
- Outside points or unreadable bounds return `invalid-argument` without mouse input; activation
  or restoration may already have occurred. `GetWindowRect` includes the invisible resize border
  (about 7-8 pixels); an in-bounds point can still hit a covering window.
- `--allowOutsideWindow` is coordinate-only and accepts true/false (bare means true). All three
  MCP tools forward explicit false with `Bool`; omitted values remain omitted. Coordinates are
  parsed and serialized invariantly, including negative values.

## Verification

- Latest CLI build succeeded. MCP and test-project builds succeeded only in the ignored scratch
  workspace `.vs\item1-final-validation-20261108-1824`, without touching running MCP outputs.
- Full updated test suite in that isolated workspace: 83 passed, 0 failed, 0 skipped. Includes
  all six normal/minimized fixture-button cases and MCP explicit false/true/omitted regressions.
- Earlier review-round live test on a distinct new Notepad window: coordinate click collapsed
  selection, double-click selected a word, right-click opened a menu, and minimized-window click
  restored the editor and collapsed selection. The test document was discarded and its window
  closed; the two pre-existing windows received no input. No new Notepad run in the final round.
- Isolated builds emitted six cached NU1900 warnings about unavailable vulnerability-data feeds;
  builds and tests succeeded without changing feeds or stopping processes.
- The graph was refreshed earlier (807 nodes, 3,801 edges); it was not regenerated for these
  review fixes. Recent symbol/caller queries reported symbol-not-found and duplicate key `Program`.

## Open Tasks / Known Issues

- Remaining requested Phase 1 items: selector-free input, wheel scrolling, a distinct dead-window
  error, and screenshot bounds/DPI metadata.
- `set-grid-cell` parity remains inconsistent; investigate internal termination and provider/layout
  timing without inferring root cause from snapshots alone.
- Cursor-session concurrent-start ownership race remains a documented limitation; cross-process
  concurrency and real cursor drawing remain unverified.
- Prior unresolved checks remain: installed vs-debug skill update, fresh-chat MCP schema checks,
  cross-process discovery MCP validation, strict-parser live VS/broker checks, and owned modeless
  windows misclassified by `isModal`. Prior graph queries also reported symbol-not-found for
  ScrollSearch/ResolveGridCell and duplicate key `Program` for caller queries.

## Recently Changed Files

- `docs\full-graph.agentdebugtoolkit.json` and `docs\project-dependencies.agentdebugtoolkit.json` — graph refresh.
- `src\AgentDebugToolkit.UiAutomation.Cli\Program.cs` — coordinate targets, post-activation bounds,
  bounded restore wait, strict override parsing, and invariant coordinates.
- `src\AgentDebugToolkit.UiAutomation.Cli\NativeMethods.cs` — minimized-state/restore calls and
  descriptive foreground errors.
- `src\AgentDebugToolkit.Mcp\UiAutomationTools.cs` — coordinate parameters, invariant forwarding,
  and explicit false for pointer-tool overrides.
- `tests\AgentDebugToolkit.UiAutomation.Tests\PointerCoordinateTargetTests.cs` and
	`McpToolMetadataTests.cs` — target/boolean validation, real-click fixtures, and MCP schemas.
- `tests\AgentDebugToolkit.UiAutomation.Tests\McpPointerCoordinateTests.cs` — negative-coordinate
  culture regressions and explicit false/true/omitted forwarding.
- `docs\CLI_CONTRACT.md` — bounds, restoration wait, override, and border semantics;
  `docs\PROJECT_STATE.md` — this session snapshot.

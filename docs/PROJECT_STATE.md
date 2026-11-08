# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-11-08: Item 1 is committed at `3e63f7d`. Implemented only Phase 1 items 4 and 5:
distinct dead-window errors and additive screenshot geometry/DPI. These changes are uncommitted.
Items 2 (selector-free input) and 3 (wheel scrolling) remain deferred.

## Current Decisions

- Shared target/scope HWND validation returns `window-gone` when `IsWindow` is false and advises
  `list-windows --processName <name>` to find the new HWND and pid. Existing hung windows retain
  `window-not-responding`; active disappearance waits retain their intended success behavior.
- Legacy failed probes recheck existence. Shared resolution returns error tuples so composite
  operations preserve their error context, including `submit-chat-message`'s resolve-window step.
- Standalone screenshots report `windowRect` (left/top/right/bottom physical screen pixels),
  `imageWidth`, `imageHeight`, and `dpiScale = GetDpiForWindow / 96.0`. The same native rectangle
  sizes the bitmap; map pixels by adding its left/top, without multiplying by DPI again.
- `screenshotPath` and `inspect --screenshot` remain unchanged. Native outer bounds include
  invisible resize borders (about 7-8 pixels at 100% DPI, scaled at higher DPI), including maximized
  windows. Capture geometry does not guarantee complete rendering by every provider.
- Item 1's post-activation bounds check and bounded restoration wait remain unchanged.

## Verification

- CLI build and isolated full solution/MCP builds succeeded in
  `.vs\items4-5-validation-20261108-1834`; no running MCP output was touched.
- Full isolated test suite: 114 passed, 0 failed, 0 skipped. Focused window checks: 26 passed;
  screenshot/metadata/legacy-inspect checks: 8 passed. Tests include closed fixtures, genuinely
  non-pumping hung fixtures, closed scopes, composite errors, and CLI/MCP normal/maximized images.
- NEW Notepad live check at DPI scale 1.5: normal rect `(-213,172,938,900)` saved 1151 x 728 pixels;
  maximized rect `(-11,-11,1931,1019)` saved 1942 x 1030 pixels. Both decoded PNG sizes exactly
  matched the native rect sizes; the maximized capture included 11-pixel invisible borders.
- The verified empty new window was closed without saving. Its old HWND returned `window-gone`
  with rediscovery guidance. Existing Notepad windows received no input; generated PNGs were removed.
- Six cached NU1900 vulnerability-feed warnings remain. Existing Console/Debugger dependency
  assets were copied into scratch to permit the full solution build; no restore/feed change needed.
- Registered graph caller lookup failed with duplicate key `Program`; direct source tracing was
  used. Pre-existing graph-file modifications were left untouched; no graph rebuild this task.

## Open Tasks / Known Issues

- Remaining Phase 1 items: selector-free input and wheel scrolling; neither was started.
- `set-grid-cell` parity remains inconsistent; investigate internal termination and provider/layout
  timing without inferring root cause from snapshots alone.
- Cursor-session concurrent-start ownership race remains a documented limitation; cross-process
  concurrency and real cursor drawing remain unverified.
- Prior unresolved checks remain: installed vs-debug skill update, fresh-chat MCP schema checks,
  cross-process discovery MCP validation, strict-parser live VS/broker checks, and owned modeless
  windows misclassified by `isModal`. Prior graph queries also reported symbol-not-found for
  ScrollSearch/ResolveGridCell and duplicate key `Program` for caller queries.

## Recently Changed Files

- `src\AgentDebugToolkit.UiAutomation.Cli\Program.cs` and `NativeMethods.cs` — shared window checks,
  window-gone errors, composite error context, and DPI query.
- `src\AgentDebugToolkit.UiAutomation.Cli\ScreenshotHelper.cs` — additive capture metadata.
- `src\AgentDebugToolkit.Mcp\UiAutomationTools.cs` — screenshot description.
- `tests\AgentDebugToolkit.UiAutomation.Tests\WindowAvailabilityTests.cs` and `ScrollContainerTests.cs`
  — closed/hung/scope regressions; `WindowScreenshotTests.cs` and `McpToolMetadataTests.cs` — image/DPI tests.
- `docs\CLI_CONTRACT.md`, `CODE_SUMMARY.md`, `KEY_FLOWS.md`, `DESIGN_DECISIONS.md`, and
  `PROJECT_STATE.md` — contracts, responsibilities, flows, decisions, and verification.

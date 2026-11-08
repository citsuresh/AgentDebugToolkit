# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-11-08: Items 4/5 are committed at `57c821f` (item 1 at `3e63f7d`). Added the requested
chat foreground-step fix, then screenshot region/scale support. These changes are uncommitted.
Current round completed focus-independent pointer bounds preflight while retaining post-activation
validation and minimized-window restoration; both requested full-suite runs passed.
Items 2 (selector-free input) and 3 (wheel scrolling) remain deferred.

## Current Decisions

- Chat input foreground handling catches only `WindowGoneException`, retaining `window-gone`
  and `step: resolve-input`. A narrow internal callback supports deterministic catch-path tests;
  production still uses the real foreground check. False/success and unrelated exceptions are preserved.
- Screenshot `--region x,y,w,h` is in original window-image pixels, including resize borders.
  Non-negative origin/positive dimensions must lie entirely inside that image; invalid input
  returns `invalid-argument` before saving. CLI parsing and MCP forwarding are invariant.
- Optional finite `--scale` is 0.1-1.0. Crop before resampling; saved dimensions use ceiling
  rounding while the transform retains the requested scale. Either option adds source `region`
  and `scale`; scale-only uses the full source region and region-only uses scale 1.
- `windowRect` remains the full physical window snapshot; saved dimensions describe the PNG.
  Map with `windowRect.left + region.x + imagePixelX / scale` (likewise y), without multiplying
  by `dpiScale`. Without options, original image/response fields and mapping are unchanged.
- `screenshotPath`, `inspect --screenshot`, and MCP's positional cancellation-token slot remain
  unchanged. Bounds include DPI-scaled invisible resize borders, including maximized windows.
  Capture geometry does not guarantee complete rendering by every provider.
- Coordinate pointer verbs preflight non-minimized root windows before activation. Outside or
  unreadable bounds return `invalid-argument` without activation/restore/input. Minimized roots
  skip preflight; the existing bounded restore wait and fresh post-activation bounds check remain.
  `--allowOutsideWindow` bypasses both checks; selectors are unchanged.

## Verification

- CLI build and isolated full solution/MCP builds succeeded in
  `.vs\region-scale-validation-20261108-1910`; no running MCP output was touched.
- Previous round's independent full run had 169 passed/3 failed, with negative-point pointer
  cases returning `foreground-activation-failed`; a focused retry passed 2/failed 1 (click).
  Current preflight fix addresses those cases without relying on activation permission.
- Current pointer-focused run: 34 passed, 0 failed, 0 skipped. Negative-point tests assert zero
  activations, state changes and mouse-downs; three added minimized cases assert restoration
	before outside-point rejection and zero mouse-downs.
- Independent final full-suite Run 1: 175 passed, 0 failed, 0 skipped (1m 24s).
  Run 2: 175 passed, 0 failed, 0 skipped (1m 21s). All three previously failing negative-point
  cases passed both runs; no retries were required. Audit found no in-scope regressions.
- Foreground catch-path tests: 4 passed; screenshot/MCP metadata tests: 60 passed.
  Includes default normal/maximized CLI/MCP parity,
  crop/scale/edges/tiny images, malformed/overflow/out-of-bounds regions, finite scale limits,
  invariant MCP formatting, every unscaled crop pixel, and scaled-region pixels.
- Phase 1 independent audit: no findings, 4 tests passed. Closure at the exact foreground step
  was not live-raced; the exception catch path was injected deterministically.
- NEW Notepad live check at DPI scale 1.5: default rect `(57,57,1208,785)` saved 1151 x 728 pixels;
  region `(20,30,101,81)` saved 101 x 81, with three source-offset pixels matching; scale 0.5 alone
  saved 576 x 364; region plus scale 0.5 saved 51 x 41. All decoded dimensions matched responses.
  An outside region returned `invalid-argument` with no image. Region/scale were not live-tested
  maximized or through an existing MCP server; MCP paths were tested with automated fixtures.
- The verified empty new window was closed without saving. Existing Notepad windows received no
  input and remained present; generated PNGs were removed. No installed MCP server was reloaded.
- Six cached NU1900 vulnerability-feed warnings remain; no restore/feed change was needed.
- Graph queries returned symbol-not-found for PointerTarget/EnsureElementForeground/ScreenshotHelper; direct
  known-source tracing was used. Pre-existing graph modifications remain untouched.

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

- `src\AgentDebugToolkit.UiAutomation.Cli\Program.cs` — chat foreground error context and region/scale CLI.
  Current round: shared pointer bounds preflight before activation for non-minimized roots.
- `src\AgentDebugToolkit.UiAutomation.Cli\ScreenshotHelper.cs` — crop/downscale and bitmap ownership.
- `src\AgentDebugToolkit.Mcp\UiAutomationTools.cs` — optional screenshot parameters and descriptions.
- `tests\AgentDebugToolkit.UiAutomation.Tests\WindowAvailabilityTests.cs` — foreground catch path;
  `WindowScreenshotTests.cs` and `McpToolMetadataTests.cs` — region/scale/default/pixel/schema regressions.
- `tests\AgentDebugToolkit.UiAutomation.Tests\PointerCoordinateTargetTests.cs` — no-activation
  negative-point assertions and minimized restore-before-bounds regressions.
- `docs\CLI_CONTRACT.md`, `CODE_SUMMARY.md`, `KEY_FLOWS.md`, `DESIGN_DECISIONS.md`, and
  `PROJECT_STATE.md` — contracts, responsibilities, flows, decisions, and verification.

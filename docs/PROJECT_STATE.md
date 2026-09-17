# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

Phase 21 added DPI-aware pointer input to `agentdebug-ui`: `drag`, `move-mouse`,
`get-cursor-pos`, `right-click`, and `double-click`. It declares per-monitor-v2 DPI awareness
at startup (with legacy fallback), uses `SendInput` for mouse buttons, bounds drag work, and
documents the physical-pixel coordinate semantics. Live Notepad validation found and fixed the
renamed `GetCursorPos` P/Invoke's missing `EntryPoint`; the selector form of `move-mouse` now
returns `window-not-responding` before UIA lookup. Committed and pushed as `2f38beb`.

`tools/Watch-CopilotChat.ps1` now uses capped adaptive polling backoff: it starts at
`-PollIntervalSeconds`, grows by `-PollBackoffMultiplier`, and caps at
`-MaxPollIntervalSeconds`. Validation rejects invalid/non-finite parameters, preserves legacy
larger initial intervals when no max is explicitly supplied, and sleeps only within the remaining
timeout budget. Committed and pushed as `1bb1505`.

## Open Tasks / Known Issues

None from this session. `docs/KNOWN_OPEN_FINDINGS.md` has no unresolved entries.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs` -- Phase 21 verb dispatch, DPI startup,
  selector/coordinate handling, and `move-mouse` responsiveness check (`2f38beb`).
- `src/AgentDebugToolkit.UiAutomation.Cli/NativeMethods.cs` -- DPI, `SendInput`, cursor query,
  drag, right-click, and double-click native helpers (`2f38beb`).
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` -- right-click and double-click helpers
  (`2f38beb`).
- `docs/CLI_CONTRACT.md`, `docs/IMPLEMENTATION_PLAN.md`, `README.md` -- Phase 21 contract and
  command-surface documentation (`2f38beb`).
- `tools/Watch-CopilotChat.ps1` -- adaptive polling backoff and validation (`1bb1505`).

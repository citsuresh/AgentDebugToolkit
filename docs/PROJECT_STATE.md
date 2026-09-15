# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

Phase 7 console automation is implemented through a detached ConPTY broker and documented for
agent use. The initial repository and README commits were pushed to `origin/main`.

## Open Tasks / Known Issues

- Validate Phase 7 exit criteria against `CAMFWDownloadConsole.exe`; current validation uses
  `cmd.exe` smoke targets only.
- Phase 2 UI Automation gaps remain: `wait-for-window-change`,
  `wait-for-process-responding`, `delay`, and `set-context` are documented but not dispatched;
  `--scopeHwnd` is ignored by `click`/`type`; and `attach --process <name>.exe` does not
  normalize the suffix.
- Continue the planned selector, navigation-map, multi-window, and Phase 8 work in
  `docs/IMPLEMENTATION_PLAN.md`.

## Recently Changed Files

- `README.md` (added GitHub project overview, quick start, and agent operating guidance)
- `docs/CLI_CONTRACT.md`, `docs/IMPLEMENTATION_PLAN.md` (Phase 7 console contract and status)
- `docs/ARCHITECTURE.md` (implemented ConPTY broker architecture and session model)
- `src/AgentDebugToolkit.ConsoleAutomation.Cli/BrokerProgram.cs` (shutdown acknowledgement and
  disconnect-safe cancellation)
- `.gitignore` (Visual Studio `.vs/` state ignored)

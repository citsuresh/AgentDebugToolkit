# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-14: Fixed UI CLI parsing so bare-value flags fail with `invalid-argument` except for
allow-listed booleans; added `--name=value`, `type --clear`, and drag source coordinates. MCP
argument forwarding now preserves empty and `--`-prefixed values, and `ui_type` exposes `clear`.
The UI CLI build passed with `DOTNET_ROOT` cleared (0 warnings, 0 errors); 11 disposable parser
checks passed. The MCP project was not built because Claude Desktop closure was not confirmed.
No live UI or MCP-level validation was performed. The Visual Studio and Console CLI parsers also
show bare-flag-to-`true` behavior; neither project was changed.

## Open Tasks / Known Issues

Confirm Claude Desktop is closed before building the MCP project. No project tests are registered
in Test Explorer; live clear behavior and drag/drop acceptance were not validated. A CLI smoke
check using a dummy HWND returned `window-not-responding` before the type-option combinations
could be verified.

The `isModal` field still treats ownership as modal state, so owned modeless windows are
misclassified; this is deferred to future multi-window/dialog-awareness work. Graph caller queries
previously failed with `Error: An item with the same key has already been added. Key: Program`;
the prior incremental graph update succeeded.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs` -- strict option parsing, `type --clear`,
  and drag source coordinates.
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` -- clear through ValuePattern or the
  synthetic keyboard fallback.
- `src/AgentDebugToolkit.Mcp/UiAutomationTools.cs` -- `--flag=value` forwarding and optional
  `ui_type` clear/text parameters.
- `docs/CLI_CONTRACT.md` -- parsing, clear, and drag response contract.
- `docs/MCP_SERVER.md` -- `ui_type` parameters and `--flag=value` behavior.
- `docs/KNOWN_OPEN_FINDINGS.md` -- resolved bare-value flag finding.
- `docs/PROJECT_STATE.md` -- updated session snapshot.

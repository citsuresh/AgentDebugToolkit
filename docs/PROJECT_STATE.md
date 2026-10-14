# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-14: MCP UI tools were aligned with the CLI contract for inspect filters, ambiguous
selectors, and `scopeHwnd` on `get-text`/`send-keys`. `Verbs.SendKeys` was verified to use the
shared `ResolveElement(windowHwnd, opts)` path. Changes were committed and pushed in `14eda15`;
the MCP project built successfully with `DOTNET_ROOT` cleared (0 warnings, 0 errors). No
MCP-level live validation was performed.

The registered graph was incrementally updated at `61a6508`: 585 nodes, 2,727 edges, 1 changed
file, in 18.23 seconds. No additional Known Open Findings were requested.

## Open Tasks / Known Issues

The `isModal` field still treats ownership as modal state, so owned modeless windows are
misclassified; this is deferred to future multi-window/dialog-awareness work.

No project tests are registered in Test Explorer. Live UI actions, real-provider filtered trees,
and screenshot capture have not been validated for this work.

Graph caller queries previously failed with `Error: An item with the same key has already been
added. Key: Program`; audits used direct source searches. The incremental graph update succeeded.

## Recently Changed Files

- `src/AgentDebugToolkit.Mcp/UiAutomationTools.cs` -- optional inspect filters and `scopeHwnd`
  forwarding for `get-text` and `send-keys`; ambiguity guidance for action tools.
- `docs/CLI_CONTRACT.md` -- documented `send-keys --scopeHwnd` behavior.
- `docs/MCP_SERVER.md` -- MCP UI filter, scoping, and ambiguity notes.
- `docs/full-graph.agentdebugtoolkit.json` -- incrementally updated graph (585 nodes,
  2,727 edges).
- `docs/project-dependencies.agentdebugtoolkit.json` -- updated solution metadata.
- `docs/PROJECT_STATE.md` -- updated session snapshot.

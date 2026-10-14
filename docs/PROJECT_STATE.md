# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-14: existing UI Automation action verbs reject ambiguous selectors through the shared
resolver, returning the total count and at most 10 candidates. `inspect` supports name/control-type
filters, ancestor-preserving tree pruning, and conditional `matchCount`. Committed and pushed as
`40dfc59`. No new verbs or edits to other projects or the skills repository.

Project memory is synced to skill v14, with a registered per-solution graph pair. End Session
updated 2 source files incrementally: 585 nodes, 2,727 edges in 29.60 seconds, based on `40dfc59`.

## Open Tasks / Known Issues

The `isModal` field still treats ownership as modal state, so owned modeless windows are
misclassified; this is deferred to future multi-window/dialog-awareness work.

No project tests are registered in Test Explorer. Targeted build and 22 disposable deterministic
parser/pruner checks passed; the invalid-control-type CLI response passed. Live UI actions,
real-provider filtered trees, and screenshot capture have not been validated for this change.

Graph caller queries failed with `Error: An item with the same key has already been added.
Key: Program`; audits used direct source searches. Graph builds succeeded. No additional Known
Open Findings entries were requested at session close.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs` -- shared ambiguity checks, `get-text`
  scope support, drag resolution ordering, and filtered inspect flags/output.
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` -- control-type validation and tree pruning.
- `docs/CLI_CONTRACT.md` -- ambiguity behavior change and filtered inspect contract.
- `docs/KNOWN_OPEN_FINDINGS.md` -- user-requested resolved silent-first-match entry.
- `.github/copilot-instructions.md`, `docs/CODE_SUMMARY.md`, `docs/KEY_FLOWS.md`, `docs/PROJECT_STATE.md`,
  `docs/full-graph.agentdebugtoolkit.json`, and
  `docs/project-dependencies.agentdebugtoolkit.json` -- skill v14 instructions, MCP summary,
  session snapshot, and refreshed graph pair. Superseded unqualified graph files were removed
  with approval or renamed during migration.

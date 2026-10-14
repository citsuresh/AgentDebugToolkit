# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-10-14: existing UI Automation action verbs reject ambiguous selectors through the shared
resolver, returning the total count and at most 10 candidates. `inspect` supports name/control-type
filters, ancestor-preserving tree pruning, and conditional `matchCount`. No new verbs or commits.

## Open Tasks / Known Issues

The `isModal` field still treats ownership as modal state, so owned modeless windows are
misclassified; this is deferred to future multi-window/dialog-awareness work.

No project tests are registered in Test Explorer. Targeted build and 22 disposable deterministic
parser/pruner checks passed; the invalid-control-type CLI response passed. Live UI actions,
real-provider filtered trees, and screenshot capture have not been validated for this change.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs` -- shared ambiguity checks, `get-text`
  scope support, drag resolution ordering, and filtered inspect flags/output.
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` -- control-type validation and tree pruning.
- `docs/CLI_CONTRACT.md` -- ambiguity behavior change and filtered inspect contract.
- `docs/KNOWN_OPEN_FINDINGS.md` -- user-requested resolved silent-first-match entry.
- `.github/copilot-instructions.md`, `docs/CODE_SUMMARY.md`, `docs/PROJECT_STATE.md`,
  `docs/full-graph.agentdebugtoolkit.json`, and
	`docs/project-dependencies.agentdebugtoolkit.json` -- earlier skill v14 bootstrap in this session;
  the graph predates the subsequent uncommitted UI Automation changes.

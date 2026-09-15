# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

This session worked through the remaining backlog in `docs/KNOWN_OPEN_FINDINGS.md`, phase by
phase, using the Regression Auditor Protocol (Pre-Build Decomposition confirmed by the user
before implementing, independent Regression Auditor subagent review after, doc updates only on
explicit user request). All entries in `docs/KNOWN_OPEN_FINDINGS.md` are now resolved except
none remain open -- the backlog is fully cleared as of this session.

**Phase 15 -- `SessionContext.Save()` `File.Move` `UnauthorizedAccessException` race.** Added
`SessionContextWriteException` and a `MoveWithRetry` helper (5 attempts, exponential backoff
20/40/80/160/320ms, retrying only on `UnauthorizedAccessException` after audit-driven narrowing
from an initially-broader `IOException` filter) to `SessionContext.cs`. Both `Save()` call sites
in `Program.cs` (`Attach`, `SetContext` verbs) now catch it and report a dedicated
`session-context-write-failed` error code instead of the generic `unhandled-exception`.
`docs/CLI_CONTRACT.md` updated (error envelope + `attach`/`set-context` failure lists). Committed
`d9f1a15`; finding marked resolved in commit `2d10430`.

**Phase 16 -- Unimplemented selector strategies (`NameRegex`/`ControlTypeIndex`/`Coordinates`)
throwing uncaught `NotSupportedException`.** Added a central `UiaHelper.
TryParseImplementedSelectorStrategy` helper (allow-listing only `Name`/`AutomationId`) and
replaced 4 raw `Enum.TryParse<SelectorStrategy>` call sites in `Program.cs` (`WaitForElement`,
`ParseSelectorArgs` for `find-first`/`find-all`, `ResolveFindScope`'s scope-strategy check,
`ResolveElement` for `click`/`type`/`get-text`/`send-keys`/`submit-chat-message`) with it, so
unimplemented strategies are now rejected centrally with `invalid-argument` at argument-parsing
time rather than surfacing as an unhandled exception. `docs/CLI_CONTRACT.md`'s `Selector` section
rewritten accordingly. Committed `3e35d60`; finding marked resolved in the same commit.

**Phase 17 -- `DteLocator` COM object leak.** `FindDte` previously never released the
`IRunningObjectTable`/`IEnumMoniker`/`IBindCtx`/per-iteration `IMoniker` RCWs (only the unmanaged
`fetchedPtr` was freed). Restructured with nested `try/finally` blocks releasing each via
`Marshal.ReleaseComObject` on every exit path, including the early `rot-unavailable`/
`devenv-not-found` returns -- the returned `DTE` object itself is deliberately left unreleased
since the caller uses it after `FindDte` returns. Committed `49f27db`; finding marked resolved in
commit `80e1f5c`.

**Phase 18 -- `JsonOutput` null-field-omission review (no code change).** Reviewed whether any
verb added since the original finding (`find-first`/`find-all`, Phase 2's new verbs, Phase 15's
`session-context-write-failed`) now depends on distinguishing JSON `null` from an absent field.
Confirmed none do -- `ToElementSummary` always substitutes `string.Empty` for null UIA properties,
Phase 2 verb payloads are all non-nullable, and `session-context-write-failed` only carries plain
string fields via `WriteError`. Left deferred as-is at the user's explicit direction; no doc or
code changes made.

**Phase 19 -- Root `nuget.config`'s `<clear/>` was solution-wide, not scoped to
`Debugger.VisualStudio`.** Deleted the repo-root `nuget.config` and added an equivalent
project-local `nuget.config` inside `src/AgentDebugToolkit.Debugger.VisualStudio/`, so only that
project's restore is scoped down to `nuget.org` (needed for EnvDTE/EnvDTE80/EnvDTE90); `Core`,
`ConsoleAutomation.Cli`, and `UiAutomation.Cli` restore against the normal machine-wide NuGet
source configuration again. Verified via `dotnet restore`/`dotnet build` on the full solution -- all
4 projects restored and built successfully. Committed `60f7d90`; finding marked resolved in commit
`309aa01`.

Earlier phases (1-14) remain implemented, committed, and pushed -- see git history
(`a3debf6`, `06850e6`, `dee237e`, `45eb0f3`, `ae45478`, `380079d`, `41bf2de`, `4178912`,
`94c5f15`) for details; unchanged this session.

## Open Tasks / Known Issues

**Currently open:** none. `docs/KNOWN_OPEN_FINDINGS.md` has no unresolved entries as of this
session (Phase 15, 16, 17, and 19 findings all marked resolved; the Phase 18 finding was reviewed
and intentionally left deferred with no action needed).

**Notable recurring environment quirks (not code issues, just operational notes for future
sessions):**
- The built-in `replace_string_in_file`/`multi_replace_string_in_file` tools intermittently
  matched against a stale cached version of `Program.cs` (and once `UiaHelper.cs`), causing large
  unintended deletions on apply. Reliable workaround used repeatedly this session: `git checkout
  --` to revert, then a direct PowerShell `[System.IO.File]::ReadAllText`/`.Replace`/`WriteAllText`
  patch, taking care to match each file's original BOM status (`Program.cs` has a BOM; most other
  files in this repo do not).
- `run_build` on `AgentDebugToolkit.UiAutomation.Cli.csproj` can fail with a locked
  `agentdebug-ui.exe` if a previous manual test run is still active -- resolved via
  `Get-Process agentdebug-ui` + `Stop-Process -Id <pid>`.
- `run_build`/`dotnet build` against the top-level `AgentDebugToolkit.slnx` can fail with
  "Project ... was not found in the current solution" via the `run_build` VS tool in some cases;
  building the specific `.csproj` (or using `dotnet build AgentDebugToolkit.slnx` directly via
  PowerShell) is the reliable fallback.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/SessionContext.cs` -- Phase 15: `SessionContextWriteException`,
  `MoveWithRetry` (5 attempts, exponential backoff, `UnauthorizedAccessException`-only retry).
  Committed `d9f1a15`.
- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs` -- Phase 15: `session-context-write-failed`
  handling at both `SessionContext.Save()` call sites (committed `d9f1a15`). Phase 16: 4 call
  sites switched to `UiaHelper.TryParseImplementedSelectorStrategy` (committed `3e35d60`).
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` -- Phase 16: added
  `ImplementedSelectorStrategies` array and `TryParseImplementedSelectorStrategy` helper.
  Committed `3e35d60`.
- `src/AgentDebugToolkit.Debugger.VisualStudio/DteLocator.cs` -- Phase 17: nested `try/finally`
  blocks releasing `rot`/`enumMoniker`/`bindCtx`/per-iteration `moniker` via
  `Marshal.ReleaseComObject`. Committed `49f27db`.
- `nuget.config` (repo root) -- Phase 19: deleted. `src/AgentDebugToolkit.Debugger.VisualStudio/
  nuget.config` -- Phase 19: added (same content, project-scoped). Committed `60f7d90`.
- `docs/CLI_CONTRACT.md` -- Phase 15 (error code + failure lists) and Phase 16 (Selector section
  rewrite) updates, committed alongside their respective code changes.
- `docs/KNOWN_OPEN_FINDINGS.md` -- Phase 15 finding marked resolved (`2d10430`); Phase 16 finding
  marked resolved (`3e35d60`); Phase 17 finding marked resolved (`80e1f5c`); Phase 19 finding
  marked resolved (`309aa01`); Phase 18 finding reviewed, left deferred with no changes.
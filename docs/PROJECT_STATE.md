# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

Previous session cleared the entire `docs/KNOWN_OPEN_FINDINGS.md` backlog (Phases 15-19). This
session implemented **Phase 20 -- breakpoint verbs & wait-for-break** for
`AgentDebugToolkit.Debugger.VisualStudio` (`agentdebug-vs`), following the same Pre-Build
Decomposition -> implement -> Regression Audit -> review -> commit discipline used for Phases
15-19, broken into 4 parts.

**Part A -- `set-breakpoint`/`list-breakpoints`.** `set-breakpoint --file <path> --line <n>
[--solution <name>]` adds a breakpoint via `Debugger.Breakpoints.Add(File:, Line:)`.
`list-breakpoints [--solution <name>]` enumerates current breakpoints. Regression Audit found
`set-breakpoint` was echoing the caller's raw input instead of the actual `Breakpoint` state VS
created -- fixed to read back `file`/`line`/`enabled` from the real `Breakpoint` object (via
`added.Item(1)`).

**Part B -- `remove-breakpoint`.** `remove-breakpoint [--file <path> --line <n>] [--all]
[--solution <name>]` removes a specific breakpoint or all of them; new `breakpoint-not-found`
error code. Regression Audit found two issues, both fixed: (1) file-path matching was
case-sensitive/non-normalized (now `StringComparison.OrdinalIgnoreCase`, matching
`DteLocator.FindDte`'s existing convention); (2) `Breakpoint`/`Breakpoints` COM RCWs obtained via
`foreach` were never released (now explicitly released via `Marshal.ReleaseComObject`,
consistent with the Phase 17 `DteLocator` COM-cleanup convention).

**Part C -- `wait-for-break`.** `wait-for-break --timeoutMs <n> [--pollMs <n>] [--solution
<name>]` polls debugger mode (via a new `GetStatusSnapshot` helper extracted from
`debugger-status`, a pure refactor with no behavior change) until break mode is entered or
timeout elapses, mirroring `wait-for-element`'s conventions in the UI automation CLI; returns the
same `mode`/`activeDocument`/`activeLine` shape as `debugger-status` (`preserveNullFields: true`).
Regression Audit raised a possible unhandled-exception risk if a poll iteration's COM call
exhausts `ComRetry`'s retries -- confirmed this is already handled by `Program.cs`'s existing
top-level `try/catch(ComBusyRetryExhaustedException)` around the whole verb dispatch (the poll
loop runs inside that scope), so a clarifying comment was added rather than a functional change.

**Part D -- docs.** Added a "Phase 20 verbs" section to `docs/CLI_CONTRACT.md` documenting all
4 verbs' full JSON contract, and rewrote `docs/IMPLEMENTATION_PLAN.md`'s Phase 20 section from
"proposed" to "implemented," summarizing the 4 parts and the audit-driven fixes applied.

**Final combined Regression Audit** (across all of Phase 20 together) found one more
cross-part consistency issue: `set-breakpoint` (Part A, written before Part B's
COM-RCW-release convention was introduced) never released its `Breakpoints`/`Breakpoint` COM
objects. Fixed for consistency with Parts B's convention.

Build verified clean both per-project (`AgentDebugToolkit.Debugger.VisualStudio.csproj`) and
solution-wide (`AgentDebugToolkit.slnx`, all 4 projects) after every part and after the final
fix. Committed and pushed as `81be2c6`.

Earlier phases (1-19) remain implemented, committed, and pushed -- see git history
(`309aa01`, `80e1f5c`, `60f7d90`, `3e35d60`, `2d10430`, `d9f1a15`, `a3debf6`, `06850e6`,
`dee237e`, `45eb0f3`) for details; unchanged this session.

## Open Tasks / Known Issues

**Currently open:** none from this session's active work. `docs/KNOWN_OPEN_FINDINGS.md` has no
unresolved entries (cleared in the previous session).

**Notable recurring environment quirks (not code issues, operational notes for future
sessions):**
- The built-in `replace_string_in_file`/`multi_replace_string_in_file` tools intermittently
  matched against a stale cached version of `Program.cs` in earlier sessions (Phases 15/16),
  requiring a `git checkout --` + direct-PowerShell-patch workaround. This did NOT recur during
  Phase 20's `Program.cs` edits this session -- all edits applied cleanly on the first attempt via
  the normal edit tools.
- `run_build` on `AgentDebugToolkit.UiAutomation.Cli.csproj` can fail with a locked
  `agentdebug-ui.exe` if a previous manual test run is still active -- resolved via
  `Get-Process agentdebug-ui` + `Stop-Process -Id <pid>` (not encountered this session, since work
  was scoped to `AgentDebugToolkit.Debugger.VisualStudio`).
- A stray `Thread.Sleep` reference inside `AgentDebugToolkit.Debugger.VisualStudio/Program.cs`
  needed explicit `System.Threading.Thread.Sleep` qualification, since `EnvDTE.Thread` is also in
  scope in that file (ambiguous-reference compiler error, caught immediately by `run_build` and
  fixed in the same edit pass).

## Recently Changed Files

- `src/AgentDebugToolkit.Debugger.VisualStudio/Program.cs` -- Phase 20: added `set-breakpoint`,
  `list-breakpoints`, `remove-breakpoint`, `wait-for-break` verbs plus their dispatch cases;
  extracted `GetStatusSnapshot(DTE dte)` helper from `debugger-status` (pure refactor, reused by
  `wait-for-break`); added `Marshal.ReleaseComObject` cleanup for all `Breakpoint`/`Breakpoints`
  COM objects touched by the new verbs; added `breakpoint-not-found` error code. Committed
  `81be2c6`.
- `docs/CLI_CONTRACT.md` -- Phase 20: new "Phase 20 verbs" section documenting all 4 new verbs'
  full JSON contract. Committed `81be2c6`.
- `docs/IMPLEMENTATION_PLAN.md` -- Phase 20 section rewritten from "proposed, not yet
  implemented" to "implemented," summarizing the 4 parts and audit-driven fixes. Committed
  `81be2c6`.
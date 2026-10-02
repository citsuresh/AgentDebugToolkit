# Known Open Findings

User-curated list of open, unresolved findings intentionally deferred rather than fixed
immediately. Entries are only added, edited, or removed at the user's explicit request.

## SessionContext.Save() File.Move can intermittently throw UnauthorizedAccessException — RESOLVED

- **First seen:** 2026-09-14
- **Last seen:** 2026-09-14
- **Occurrences:** 1
- **Resolved:** 2026-09-15 (commit `d9f1a15`, Phase 15)

**Description:** During the Phase 2 Part 1 regression audit of the `attach`/session-context
concurrency fix, a Regression Auditor subagent reproduced (in an isolated stress-test harness,
not part of the reviewed diff) that `SessionContext.Save()`'s
`File.Move(temporaryPath, FilePath, overwrite: true)` can intermittently throw
`UnauthorizedAccessException` — reproduced even with a single writer thread and zero concurrent
readers (66-201 failures per 2000 iterations, in both `%TEMP%` and a repo-local directory). This
is independent of the `FileShare` flags used by `Load()` (unaffected by that fix). When it
occurs, it propagates past `Save()` (which has no internal try/catch) to `Program.cs`'s
top-level exception handler, and `attach` reports a generic `"unhandled-exception"` instead of a
specific, actionable error.

**Why deferred:** `attach` is a single, infrequent, user/agent-initiated call — the failure
surfaces as a catchable (if unhelpful) error rather than data corruption or a hang, so it is not
blocking current Phase 2 work.

**Fix applied (commit `d9f1a15`):** `SessionContext.Save()`'s `File.Move` now goes through a
`MoveWithRetry` helper that retries up to 5 times with exponential backoff (20/40/80/160/320ms,
~630ms worst case) specifically on `UnauthorizedAccessException`. If all retries are exhausted, a
new `SessionContextWriteException` is thrown instead of letting the raw exception propagate. Both
`SessionContext.Save` call sites (`attach`, `set-context`) catch this specifically and report the
dedicated `session-context-write-failed` error code instead of the generic `unhandled-exception`.
`docs/CLI_CONTRACT.md` updated with the new error code under both verbs. Regression-audited (one
pass, narrowing the retry filter from the broader `IOException` hierarchy down to
`UnauthorizedAccessException` only, since that was the actual observed failure mode).

## JsonOutput.WriteSuccess omits null fields by default instead of emitting JSON `null`

- **First seen:** 2026-09-14
- **Last seen:** 2026-09-14
- **Occurrences:** 1

**Description:** `JsonOutput.Options` sets `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`,
so any anonymous-object property with a null value passed to `WriteSuccess`/`WriteError` is
omitted from the emitted JSON entirely, rather than written as `"field": null`. Surfaced during
the Phase 2 Part 2 regression audit of `wait-for-element`, whose documented `disappeared`
success case relies on `elementFound` being explicitly `null` (not absent). A scoped fix
(`WriteSuccess(payload, preserveNullFields: true)`) was added and used by `wait-for-element`
specifically, but the default behavior for all other verbs (e.g. `inspect`'s `screenshotPath`)
is unchanged and still silently drops null fields.

**Why deferred:** No other current verb's documented contract treats a missing key vs. an
explicit JSON `null` as meaningfully different, so this isn't blocking outside of
`wait-for-element` (already addressed there). Changing the shared default risks altering output
shape for every existing verb without a concrete need.

**Suggested fix (not yet applied):** If a future verb's contract needs `null` to be
distinguishable from "field absent," use the existing `preserveNullFields: true` opt-in on that
call, or revisit whether `JsonOutput`'s default should change globally once more than one verb
depends on it.

## Unimplemented selector strategies throw an uncaught NotSupportedException — RESOLVED

- **First seen:** 2026-09-14
- **Last seen:** 2026-09-14
- **Occurrences:** 1
- **Resolved:** 2026-09-15 (Phase 16)

**Description:** `SelectorStrategy` enum already includes `NameRegex`, `ControlTypeIndex`, and
`Coordinates` (reserved for Phase 3), so `Enum.TryParse<SelectorStrategy>` accepts them as valid
`--strategy` values today even though `UiaHelper.ResolveSelector` throws `NotSupportedException`
for all three. None of `click`/`type`/`get-text`/`wait-for-element` catch this locally, so it
propagates to `Program.cs`'s top-level handler and surfaces as `"unhandled-exception"` instead
of a clean `invalid-argument` (or a dedicated `not-supported`) error. Surfaced during the Phase 2
Part 2 regression audit of `wait-for-element`, but it is a pre-existing gap shared by every
selector-consuming verb, not something introduced by that change.

**Why deferred:** Phase 3 is where these strategies get real implementations; until then, a
clear one-line fix is to reject them at argument-parsing time in each verb (or centrally), but
that's cheap enough to fold into Phase 3's own selector-strategy work rather than doing it twice.

**Fix applied (Phase 16):** Added a central helper,
`UiaHelper.TryParseImplementedSelectorStrategy(text, out strategy, out error)`, that parses
`--strategy`/`--scopeStrategy` via `Enum.TryParse` and additionally checks membership in a
private `ImplementedSelectorStrategies` allow-list (currently `Name`, `AutomationId` — the exact
set `ResolveSelector`/`ResolveSelectorAll` implement). All four call sites that previously called
`Enum.TryParse<SelectorStrategy>` directly (`WaitForElement`; `ParseSelectorArgs`, shared by
`find-first`/`find-all`; `ResolveFindScope`'s `--scopeStrategy` check; `ResolveElement`, shared by
`click`/`type`/`get-text`/`send-keys`/`submit-chat-message`) now go through this helper, rejecting
`NameRegex`/`ControlTypeIndex`/`Coordinates` with a clean `invalid-argument` instead of reaching
`ResolveSelector`'s `NotSupportedException`. `docs/CLI_CONTRACT.md`'s `Selector` section updated
accordingly. Regression-audited (one pass, no in-scope issues found — a pre-existing
missing-vs-invalid message-wording ambiguity was flagged but classified out-of-scope for this
phase, since it predates this change and Phase 16's goal was only to prevent the uncaught
exception, not redesign the error text).

**Suggested fix (not yet applied):** When Phase 3 implements `NameRegex`/`ControlTypeIndex`/
`Coordinates`, either implement them (removing the throw) or, if any are implemented later than
others, explicitly reject the not-yet-implemented ones with `invalid-argument` at the same
validation point where `--strategy` is currently parsed in each verb.

## DteLocator ROT/moniker COM objects are not explicitly released — RESOLVED

- **First seen:** 2026-09-14
- **Last seen:** 2026-09-14
- **Occurrences:** 1
- **Resolved:** Phase 17, commit `49f27db`

**Description:** In `DteLocator.FindDte`, the `IRunningObjectTable`, `IEnumMoniker`, `IBindCtx`,
and each per-iteration `IMoniker` RCW are never released via `Marshal.ReleaseComObject`/
`FinalReleaseComObject` — only the unmanaged `fetchedPtr` (`Marshal.AllocHGlobal`) is freed in a
`finally`. Surfaced during the Phase 6 regression audit.

**Why deferred:** `agentdebug-vs` is a one-shot CLI process that exits immediately after `Main`
returns, so the OS/CLR reclaims these on process exit; there is no observable leak in current
usage. It would only matter if this code were reused in-process/repeatedly (e.g. hosted as a
library called many times without a process restart).

**Fix applied (Phase 17):** `FindDte` now nests `try/finally` blocks around `rot`, `enumMoniker`,
`bindCtx`, and each per-iteration `moniker`, releasing each via `Marshal.ReleaseComObject` on
every exit path (including the early `rot-unavailable`/`devenv-not-found` returns). The returned
`DTE` object itself is intentionally left unreleased, since it is used by the caller after
`FindDte` returns. A Regression Auditor review found no in-scope issues; build succeeded with 0
warnings/errors; no test project exists for this code.

## ComRetry's COM-busy retry/backoff path: live-triggered, isolated, and fixed — RESOLVED

- **First seen:** 2026-10-02
- **Last seen:** 2026-10-02
- **Occurrences:** 1
- **Resolved:** 2026-10-02 (same session)

**Description:** `ComRetry`'s bounded retry-with-backoff around `RPC_E_SERVERCALL_RETRYLATER` /
`RPC_E_CALL_REJECTED` / `RPC_E_SERVERCALL_REJECTED` / `RPC_E_CALL_COMPLETE` (surfacing
`com-busy-retry-exhausted` on exhaustion) was live-triggered via 20-30 parallel PowerShell jobs
calling `debugger-status`/`list-threads` against the same attached VS instance simultaneously. The
real busy HRESULT (`0x8001010A RPC_E_SERVERCALL_RETRYLATER`) fired repeatedly and reliably under
this load. The first attempt showed 34/200 (17%) of calls surfacing the busy HRESULT as a raw
`unhandled-exception` instead of being retried — traced to `DteLocator.EnumerateDteInstances()`'s
`rot.EnumRunning`/`enumMoniker.Reset`/`enumMoniker.Next` calls and bare `dte.Debugger`/`thread.ID`/
`thread.Name` property reads bypassing `ComRetry`. Wrapping those reduced the leak to 4/250 (1.6%).

**Root cause of the remaining 1.6%, isolated via temporary diagnostic logging:** added a
temporary `catch` block logging the call site/stack trace whenever a busy-HRESULT `COMException`
escaped uncaught, then re-ran the same stress test. Caught the exact site on the first repro: the
C# compiler's implicit `foreach (EnvDTE.Thread thread in threadsCollection)` over
`EnvDTE.Threads` calls `EnvDTE.Threads.GetEnumerator()` internally — this is itself a cross-process
COM call into devenv and threw `RPC_E_SERVERCALL_RETRYLATER` directly from
`ListThreads`'s `foreach`, bypassing `ComRetry` entirely since `foreach`'s enumerator acquisition
is compiler-generated and was never passed through `ComRetry.Invoke`. The two P/Invoke-based
suspects named in the original finding (`GetRunningObjectTable`/`CreateBindCtx`) were **ruled out**
— they never appeared in the diagnostic log across any repro run.

**Fix applied:** Replaced the `foreach` in `ListThreads` with manual
`ComRetry.Invoke(() => threadsCollection.GetEnumerator())` + `ComRetry.Invoke(() =>
enumerator.MoveNext())` calls, so both the enumerator acquisition and each iteration step go
through the same retry/backoff path as every other EnvDTE call.

**Verification:** removed the temporary diagnostic logging after isolating the cause, rebuilt (0
warnings/errors), then re-ran the same concurrent stress test twice more: 250/250 and 360/360
calls succeeded with 0 leaked `unhandled-exception` and 0 `com-busy-retry-exhausted` (610 total
calls, 0 failures of either kind) — a reliable real busy condition was both triggered and
correctly retried/surfaced end to end.

**Residual caveat (RESOLVED as of 2026-10-02, broader fix):** the original fix above covered only
the `debugger-status`/`list-threads` call path. A follow-up pass added a shared
`ComRetry.ForEach<T>` helper (two overloads: always-exhaust and early-exit) and converted every
remaining `foreach` over an EnvDTE/COM collection in the project to use it: `GetCallStack`'s
`thread.StackFrames` loop, `GetLocals`'s and `GetExceptionInfo`'s `stackFrame.Locals` loops,
`SelectFrame`'s frame-resolution loop, `ResolveThreadById`'s thread-search loop,
`ListBreakpoints`'s and `RemoveBreakpoint`'s `breakpointsCollection` loops, and
`DteLocator.FindProcessByPid`'s `processes` loop. (`RemoveBreakpoint`'s final loop over its
already-materialized `List<Breakpoint> candidates`, and `DteLocator`'s loops over plain
`List<(DTE,...)>` tuples, were confirmed to NOT need conversion since they iterate managed
collections, not live COM collections.)

**Verification of the broader fix:** rebuilt clean (0 warnings/errors), re-attached to the same
live WPF app, set a real breakpoint, and ran two 25-30-parallel-job concurrent stress tests
exercising `debugger-status`, `list-threads`, `get-callstack`, `select-thread`, `select-frame`,
`get-locals`, and `list-breakpoints` together (not just the original two verbs) against a real
break-mode session with real stack frames/locals/breakpoints. Results: 1540 total calls across both
runs, 0 leaked `unhandled-exception`, 8 calls correctly surfaced as `com-busy-retry-exhausted`
(genuine retry-budget exhaustion under heavy contention, not a ComRetry-bypass leak). This is now
considered fully resolved across the whole project, not just the originally-reproduced
`debugger-status`/`list-threads` path.

## select-frame throws COMException("Element not found.") on the WPF UI/dispatcher thread

- **First seen:** 2026-10-02
- **Last seen:** 2026-10-02
- **Occurrences:** 1

**Description:** discovered incidentally while verifying the ComRetry fix above (not a ComRetry
issue itself). `select-frame` against a specific thread's frames (the WPF UI/dispatcher thread,
thread id 95012 in the test app, `AttendenceChecker.UI.exe`) consistently raised
`EnvDTE.Debugger.set_CurrentStackFrame` → `COMException("Element not found.")` for every frame
index on that thread (0 through 5, including the `[Managed to Native Transition]` frame and every
managed frame above it), reproducing identically with zero concurrency (sequential single calls,
no stress test involved). Confirmed via stack trace that this is a genuine `COMException` thrown
directly from the property setter itself, not a `ComRetry`-bypassed enumerator call (ruled out as
unrelated to the busy-HRESULT retry path). Selecting frames on a different thread in the same
process (thread id 124504, `.NET Timer`) worked without error. Not investigated further at the
time since it was out of scope for the ComRetry fix in progress.

## Root nuget.config's `<clear/>` is solution-wide, not scoped to the new project — RESOLVED

- **First seen:** 2026-09-14
- **Last seen:** 2026-09-14
- **Occurrences:** 1
- **Resolved:** Phase 19, commit `60f7d90`

**Description:** The repo-root `nuget.config` (added to unblock restoring `EnvDTE`/`EnvDTE80`/
`EnvDTE90` packages, since the machine-wide NuGet config points at private Azure DevOps feeds
that return 401 Unauthorized for unauthenticated restores) clears package sources down to
`nuget.org` only for the entire repo tree, not just
`AgentDebugToolkit.Debugger.VisualStudio`. Confirmed via a clean build that `Core` and
`UiAutomation.Cli` are unaffected today (neither has any `PackageReference`), but any future
package reference added to those projects that only resolves from the machine's private feeds
would silently fail restore, with the root cause (a repo-root config added for an unrelated
project) not obvious from the failing project.

**Why deferred:** No current project depends on the private feeds, so there is no active
regression; this is a latent, forward-looking risk only.

**Fix applied (Phase 19):** Removed the repo-root `nuget.config` and replaced it with a
project-local `nuget.config` inside `src/AgentDebugToolkit.Debugger.VisualStudio/`, containing
the same `<clear/>` + `nuget.org`-only source list. NuGet's hierarchical config resolution scopes
this restriction to just that project's restore, per-project, during a solution-wide
build/restore — verified via `dotnet restore`/`dotnet build` on the full solution: all 4 projects
restored and built successfully, with `Debugger.VisualStudio` still resolving EnvDTE/EnvDTE80/
EnvDTE90 from nuget.org and the other three projects restoring against the normal machine-wide
NuGet source configuration. A Regression Auditor review found no in-scope issues.

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

## DteLocator ROT/moniker COM objects are not explicitly released

- **First seen:** 2026-09-14
- **Last seen:** 2026-09-14
- **Occurrences:** 1

**Description:** In `DteLocator.FindDte`, the `IRunningObjectTable`, `IEnumMoniker`, `IBindCtx`,
and each per-iteration `IMoniker` RCW are never released via `Marshal.ReleaseComObject`/
`FinalReleaseComObject` — only the unmanaged `fetchedPtr` (`Marshal.AllocHGlobal`) is freed in a
`finally`. Surfaced during the Phase 6 regression audit.

**Why deferred:** `agentdebug-vs` is a one-shot CLI process that exits immediately after `Main`
returns, so the OS/CLR reclaims these on process exit; there is no observable leak in current
usage. It would only matter if this code were reused in-process/repeatedly (e.g. hosted as a
library called many times without a process restart).

**Suggested fix (not yet applied):** Wrap `rot`/`enumMoniker`/`bindCtx`/`moniker` releases in a
`finally` block using `Marshal.ReleaseComObject`, or explicitly document that cleanup currently
relies on process-exit teardown.

## Root nuget.config's `<clear/>` is solution-wide, not scoped to the new project

- **First seen:** 2026-09-14
- **Last seen:** 2026-09-14
- **Occurrences:** 1

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

**Suggested fix (not yet applied):** Scope the `<clear/>`/source override to just the
`Debugger.VisualStudio` project (e.g. via a project-local `nuget.config` in that project's own
folder instead of the repo root), or restore the machine's private feeds alongside `nuget.org` at
the repo root so other projects keep access to them.

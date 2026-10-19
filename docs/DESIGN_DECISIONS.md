# Design Decisions

Append-only, dated log of non-obvious architectural/design choices. Never delete or rewrite
prior entries — reversed decisions get a new entry referencing the old one.

## 2026-09-14 — Synthetic input as primary interaction mechanism

**Decision:** `UiaHelper.Click`/`Type` attempt UIA patterns (Invoke/Toggle/Value) first, but
fall back to synthetic mouse/keyboard input (`NativeMethods`) rather than treating pattern
absence as an error.

**Rationale:** Per `docs/VALIDATION_FINDINGS.md`, most elements in the validated target app
support no UIA patterns at all, so synthetic input is effectively the primary mechanism, not
just a fallback.

**Alternatives considered:** Requiring UIA pattern support and failing otherwise — rejected as
it would make the tool unusable against the target application class it's designed for.

## 2026-09-14 — Cross-process session state via temp file

**Decision:** `SessionContext` persists the attached pid to a JSON file under
`%TEMP%\agentdebugtoolkit\ui-session.json` instead of in-memory state.

**Rationale:** Each CLI invocation is a fresh process, so there is no in-process way to
remember the "current" pid between an `attach` call and subsequent verb calls without an
external store.

**Alternatives considered:** Requiring `--pid` on every call — rejected for CLI ergonomics;
supported as an override via `ResolvePid` regardless.

## 2026-09-15 — Detached broker owns ConsoleAutomation ConPTY state

**Decision:** `agentdebug-console` launches a detached broker process that owns the ConPTY
handle, target process, host pipes, terminal buffer, and named-pipe server; public CLI verbs are
short-lived pipe clients using persisted broker session data.

**Rationale:** ConPTY handles and continuously drained output cannot be transferred safely between
independent CLI invocations. A broker preserves interactive terminal state while the public CLI
remains simple and independently deployable from the UI Automation and Visual Studio components.

**Alternatives considered:** Keeping all state in each public invocation — rejected because it
would lose the target's terminal state and output stream. Adding a dependency on the other CLI
projects — rejected to keep console automation isolated from their process/UIA/EnvDTE concerns.

## 2026-09-18 — Win32 is the authoritative desktop-window enumerator

**Decision:** Enumerate visible windows for a target process through Win32 `EnumWindows`, then use
their HWNDs as the UIA roots for interaction.

**Rationale:** UIA's desktop-child tree can omit owned native dialogs, including WPF
`MessageBox.Show` windows (`#32770`), while `EnumWindows` returns them reliably.

**Alternatives considered:** Retaining UIA `TreeScope.Children` enumeration — rejected because it
cannot surface these dialogs for discovery, screenshots, or button interaction.

## 2026-09-19 — UIA grid patterns over persisted DataGrid coordinates

**Decision:** `set-grid-cell` identifies virtualized grid rows by a stable descendant selector and
cells by `GridItemPattern` column, then resolves the realized in-cell editor.

**Rationale:** Virtualization and reflow make coordinates captured by a prior inspection unstable.
UIA scroll, selection, grid, value, and invoke patterns provide a current, structural path.

**Alternatives considered:** Coordinate-based grid clicking — rejected because it was flaky in
live WindowWorks validation and cannot reliably distinguish a cell from its in-place editor.

## 2026-10-14 -- Strict public option parsing and shared MCP value formatting

**Decision:** Public CLI parsers accept bare flags only for their explicit boolean allow-lists;
empty and `--`-prefixed values use `--name=value`. MCP builders share a single formatter while
the console broker keeps its reconstructed strict flag/value pairs.

**Rationale:** Missing native arguments must not silently become `true` or consume subsequent
options. Shared formatting preserves values consistently without changing boolean evaluation,
launch gates, or the broker's internal protocol.

**Alternatives considered:** Retaining console's `--args` swallowing exception or duplicating
MCP formatting in each builder -- rejected because both allow inconsistent option handling.

## 2026-10-14 -- Broker must not inherit the caller's standard handles

**Decision:** `ConsoleVerbs.StartBroker` clears `HANDLE_FLAG_INHERIT` on the CLI's own standard
handles before `Process.Start`, keeping the broker's private redirected stdio.

**Rationale:** `Process.Start` inherits all inheritable handles, so the long-lived broker kept the
caller's output pipe open; callers that capture output (including MCP `CliRunner`) waited for EOF
until the broker exited.

**Alternatives considered:** Launching the broker via native `CreateProcessW` with inheritance
disabled -- larger change. Making `CliRunner` stop waiting for EOF -- affects every VS/UI tool,
can truncate output, and leaves non-MCP callers affected.

## 2026-10-16 -- Cross-process window discovery is read-only and capped

**Decision:** `list-windows` / `wait-for-window-change` gain `--all` and `--processName` scopes
(mutually exclusive with `--pid`) plus title/class filters, via one shared
`UiaHelper.QueryTopLevelWindows` still built on Win32 `EnumWindows` (see 2026-09-18). Discovery
never changes session context. Only cross-process modes exclude cloaked, untitled (unless
`--includeUntitled`), and zero-area windows by default. `list-windows` caps returned results with
`--limit` (default 50, max 200) in every scope; `wait-for-window-change` caps returned lists only
in cross-process modes while polling the full set. Pid/context modes keep their unfiltered
results, and
`ListTopLevelWindows(int pid)` is unchanged for its existing callers.

`wait-for-window-change` behavior notes: in settle mode, `windowsAfter` now reflects the latest
poll (the snapshot is refreshed every poll rather than only when the set changes; since success
requires an unchanged hwnd set, this differs only in per-window details such as title or bounds).
Wait responses (success and timeout) always include `truncated`, including pid/context modes
where it is always false.

**Rationale:** Callers need to find windows opened by other processes without guessing a pid.
Keeping discovery read-only avoids silently retargeting later pid-based verbs. Desktop-wide
enumeration otherwise returns many invisible-in-practice (cloaked/shell) windows and every
window title on the desktop; default exclusions plus a cap limit noise and accidental exposure of
sensitive titles.

**Alternatives considered:** Having discovery set the session context -- rejected, surprising
side effect for a read-only tool. Applying the default exclusions in pid mode -- rejected,
changes existing results. UIA desktop-tree enumeration -- rejected for the same reasons as the
2026-09-18 decision.

## 2026-10-16 -- Scroll targets with UIA patterns only

**Decision:** `scroll-into-view` uses UIA `ScrollPattern`, `ScrollItemPattern`, and
`ItemContainerPattern`; it does not request foreground activation, makes no cursor or input calls,
and does not restore the scroll position. The target's UI Automation provider may still activate its
window during a scroll (observed with WPF; see `KNOWN_OPEN_FINDINGS.md`). The wheel fallback is
deferred.

**Rationale:** UIA scrolling is structural and can realize virtualized items without injecting
mouse input or changing foreground state. Returning the resulting scroll position lets callers
inspect the target in place; automatic restoration could hide it again or race with virtualization.

**Alternatives considered:** Foreground activation and mouse-wheel input -- rejected for this verb
because UIA-only behavior is predictable and avoids interacting with another application.

**Observed WPF results:** `FindItemByProperty` hit both Name and AutomationId on the synthetic
virtualizing list, and `Realize` succeeded; far targets reported `item-container`. In a separate
56-target scroll-search comparison, `large` found 56 and `small` found 48; a focused post-fix
`small` check found a target in 54 steps. The comparison showed provider-dependent step behavior,
not parity across all targets.

## 2026-10-19 -- scroll-into-view error handling fix-up

**Decision:** (1) Provider `InvalidOperationException`s raised during scroll-into-view (other than
`ElementNotAvailableException`, which stays `stale-context` and is caught first) map to
`scroll-provider-error` with `details.stage`, instead of reaching the top-level
`unhandled-exception` handler. (2) The item-container lookup rethrows
`ElementNotAvailableException` so it reports `stale-context` rather than silently falling through
to scroll-search; other `InvalidOperationException`/`ArgumentException` still fall through.
(3) The `not-scrollable` message distinguishes "no ScrollItemPattern" from "ScrollItemPattern
present but still not visible". (4) Both vertical percent fields are present on every response
after window/scope lookup (`-1` when no vertical scroller is known); early validation and window
errors omit them.

**Rationale:** Callers get a documented code and a stable payload shape for every scroll-stage
outcome, and a vanished element is not misreported as a search miss.

**Alternatives considered:** Catching all exceptions -- rejected; non-UIA bugs should still surface
as `unhandled-exception`.

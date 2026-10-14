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

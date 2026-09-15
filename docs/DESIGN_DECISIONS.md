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

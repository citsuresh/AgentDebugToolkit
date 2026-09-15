# CLI Contract — AgentDebugToolkit.UiAutomation.Cli

All commands are invoked as `agentdebug-ui <verb> [options]` and print one JSON object to stdout.
Exit code 0 = success, non-zero = failure (with `"error"`/`"message"` fields populated).

This document covers Phases 1-4 verbs (see `IMPLEMENTATION_PLAN.md`). Extend this file as new
verbs are added in later phases — do not silently diverge from what's documented here.

## Common types

### Selector
```json
{ "strategy": "Name" | "AutomationId" | "NameRegex" | "ControlTypeIndex" | "Coordinates",
  "value": "string" }
```
- `Name`: exact match against `AutomationElement.Current.Name`.
- `AutomationId`: exact match against `AutomationId` property (works for some target apps, not
  reliably for the FDM app family — see VALIDATION_FINDINGS.md).
- `NameRegex`: value is a regex tested against `Name`. (Phase 3)
- `ControlTypeIndex`: value format `"<ControlType>:<index>"`, e.g. `"Button:2"` — the nth matching
  control (0-based) among descendants of the scope. (Phase 3)
- `Coordinates`: value format `"x,y"`, **client-area-relative** to the scope window. (Phase 3)

### WindowInfo
```json
{ "hwnd": "0x00123456", "title": "Field Deployment Manager", "className": "WindowsForms10...",
  "pid": 12345, "ownerHwnd": "0x00000000" | null, "isModal": false, "isForeground": true,
  "boundingRect": { "x": 21, "y": 69, "width": 1253, "height": 900 } }
```
(`ownerHwnd`/`isModal` fields added in Phase 4; omit or default false/null before then.)

`boundingRect` is omitted entirely (not emitted as JSON `null`, per `JsonOutput`'s default
null-field-omission behavior) when UIA reports a non-finite (`NaN`/`Infinity`) bounding rectangle
for the window — this can legitimately occur for offscreen, virtualized, or not-yet-realized
elements. Callers must treat a missing `boundingRect` key as "bounds unavailable", not as an
error.

### ElementInfo (used by `inspect`, `read-visible-text`)
```json
{ "name": "OK", "controlType": "ControlType.Pane", "className": "WindowsForms10...",
  "automationId": "18750130", "isEnabled": true, "isOffscreen": false,
  "boundingRect": { "x": 1116, "y": 882, "width": 90, "height": 36 },
  "supportedPatterns": ["InvokePatternIdentifiers.Pattern"],
  "children": [ /* ElementInfo[] */ ] }
```
`boundingRect` is omitted entirely (same rule as `WindowInfo` above) when the element's
`BoundingRectangle` contains non-finite values, instead of serializing invalid numeric data or
throwing. `inspect` remains successful in this case; only the affected element(s) lack
`boundingRect`.

### Error envelope (on failure, any verb)
```json
{ "success": false, "error": "element-not-found" | "stale-context" | "ambiguous-process" |
  "ambiguous-window" | "timeout" | "process-not-responding" | "window-not-responding" |
  "invalid-argument", "message": "human readable detail" }
```

---

## Phase 1 verbs

### `attach --process <name>`
Resolves a running process by exact name match (no `.exe` suffix assumed either way — match
flexibly). Persists session context.
- Success: `{ "success": true, "pid": 145376, "processName": "Fdm", "windows": [WindowInfo, ...] }`
- Failure: `ambiguous-process` (include `"candidates": [{pid, title}, ...]`) or
  `"process-not-found"`.

### `list-windows [--pid <n>]`
Uses context pid if `--pid` omitted.
- Success: `{ "success": true, "windows": [WindowInfo, ...] }`

### `inspect --hwnd <h> [--maxDepth <n>] [--screenshot true|false]`
Dumps the UIA subtree rooted at the given window/element handle, and optionally saves a
screenshot.
- Success: `{ "success": true, "root": ElementInfo, "screenshotPath": "C:\\...\\shot_20260914...png" | null }`
- `--maxDepth` default: unbounded within reason (cap at e.g. 12 to avoid runaway trees); document
  actual default once implemented.
- Screenshots save to a standard folder, e.g. `%LOCALAPPDATA%\AgentDebugToolkit\screenshots\`.

### `click --hwnd <h> --strategy <s> --value <v> [--scopeHwnd <h2>]`
Resolves the element via selector (scoped to `--scopeHwnd` if given, else `--hwnd`), attempts
`InvokePattern`/`TogglePattern`, falls back to synthetic click at `BoundingRectangle` center.
- Success: `{ "success": true, "method": "pattern" | "synthetic-click", "elementFound": ElementInfo }`
- Failure: `element-not-found` if selector resolves to nothing.

### `type --hwnd <h> --strategy <s> --value <v> --text <input> [--scopeHwnd <h2>]`
Resolves element, attempts `ValuePattern.SetValue`, falls back to click-to-focus + synthetic
keyboard input.
- Success: `{ "success": true, "method": "pattern" | "synthetic-keyboard" }`

### `get-text --hwnd <h> --strategy <s> --value <v>`
Reads current `Name` or `ValuePattern.Value` (whichever is more appropriate/available) of the
resolved element.
- Success: `{ "success": true, "text": "10.176.100.248" }`

---

## Phase 2 verbs

### `wait-for-window-change --pid <n> --timeoutMs <n> [--settleMs 300]`
Snapshots `list-windows` immediately, polls until the window set is stable for `settleMs`
consecutive milliseconds, or `timeoutMs` elapses.
- Success: `{ "success": true, "windowsBefore": [WindowInfo,...], "windowsAfter": [WindowInfo,...],
  "newWindows": [WindowInfo,...], "closedWindows": [WindowInfo,...], "elapsedMs": 480 }`
- Failure: `timeout` (includes the same fields captured at timeout, best-effort).

### `wait-for-element --strategy <s> --value <v> [--hwnd <h> | --pid <n>] [--state appeared|disappeared|enabled] [--timeoutMs <n>] [--pollMs <n>]`
Resolves the target window **once, up front**, the same way as `click`/`type` (explicit `--hwnd`,
else `--pid`, else the persisted session context) — a resolution failure here (bad `--hwnd`/
`--pid`, no session context, ambiguous window) fails immediately and is not retried across the
poll loop. `--timeoutMs` (default `5000`) must be a non-negative integer and `--pollMs` (default
`250`) must be a positive integer, validated before polling begins. Then polls the given selector
within that window until it reaches `--state` (default `appeared`) or `--timeoutMs` elapses.
- `appeared`: element resolves via the selector (scope window assumed to persist).
- `disappeared`: the scope window itself closes, or the element no longer resolves within it.
- `enabled`: element resolves and `IsEnabled` is true (scope window assumed to persist).
- Success: `{ "success": true, "elementFound": ElementInfo | null, "elapsedMs": 210 }`
  (`elementFound` is always present, explicitly `null` only for a satisfied `disappeared` wait —
  this verb's success payload preserves null fields rather than omitting them.)
- Failure: `timeout` if the state is never reached in time; otherwise the same window-resolution
  errors as `click`/`type` (`invalid-argument`, `ambiguous-window`, `stale-context`,
  `element-not-found`), returned immediately without polling.

### `wait-for-process-responding --pid <n> --timeoutMs <n>`
Uses `SendMessageTimeout` (or equivalent) hang detection.
- Success: `{ "success": true, "responding": true, "elapsedMs": 15 }`
- Failure: `"process-not-responding"` if it never responds within timeout.

### `delay --ms <n>`
- Success: `{ "success": true, "waitedMs": 500 }`

### `set-context --pid <n>`
Manual session-context override.
- Success: `{ "success": true, "pid": 145376 }`

---

## Phase 3 additions

- New selector strategies (`NameRegex`, `ControlTypeIndex`, `Coordinates`) usable in `click`,
  `type`, `get-text`, `wait-for-element` — no new verbs, just expanded `--strategy` values.
- `--scopeHwnd` becomes documented/required-by-convention on all element-resolving verbs once
  multi-window scenarios matter (Phase 4), to avoid accidental desktop-wide search.

## Phase 4 additions

- `list-windows` output gains accurate `ownerHwnd`/`isModal`/`isForeground` (may be stubbed
  false/null in Phase 1-3).

## Phase 8 verbs (read-only IDE chat observation)

These verbs never activate/focus the target window, invoke a UIA pattern, click, type, or
otherwise alter target process/window state. They are strictly read-only observation helpers,
added to let the agent inspect visible text and capture screenshots from an existing window
(e.g. a Visual Studio Copilot Chat panel) without interacting with it.

### `read-visible-text --hwnd <h> [--maxDepth <n>]`
Traverses the UIA subtree rooted at the given window handle (bounded depth/breadth/node-count,
mirroring `inspect`'s traversal caps) and returns de-duplicated, ordered visible text extracted
opportunistically from `TextPattern`, `ValuePattern`, and element `Name`. `--maxDepth` defaults
to `8` (same default as `inspect`) and must be a non-negative integer if given. Internally capped
at 200 children per element, 5000 total visited nodes, and 2000 characters per extracted text
value; `truncated` is `true` if any of these caps were hit, so callers can distinguish a
genuinely exhaustive result from a capped one.
- Success: `{ "success": true, "lines": ["...", "..."], "truncated": false }`
- Failure: `invalid-argument`, `stale-context`, `ambiguous-window`, `element-not-found` (same
  window-resolution errors as `inspect`), or `window-not-responding` (checked up front, since
  UIA calls against a hung target's message pump can otherwise block).
- Validated live (2026-09-15) against a real Visual Studio Insiders window's Copilot Chat panel:
  successfully returned the actual chat transcript text (prompt, responses, UI chrome), including
  a `truncated: true` result on that window given its large accessibility tree. Confirms Copilot
  Chat content is UIA-exposed in this environment; the `screenshot` fallback below is not required
  for that specific content, but remains available for chat/webview UIs where it is not.

### `screenshot --hwnd <h>`
Captures a bitmap of the given window and returns the saved PNG path — a fallback observation
method for content not meaningfully exposed via UIA (e.g. custom controls or webview-hosted
content). Does not activate/focus the window.
- Capture method: attempts `PrintWindow` with `PW_RENDERFULLCONTENT` first, which renders the
  target window's own content directly (needed for modern DirectComposition/WPF-rendered windows,
  e.g. Visual Studio's own UI) regardless of z-order or whether the window is obscured by other
  windows on screen. Falls back to a `CopyFromScreen` capture of the window's screen rect (via
  `GetWindowRect`) only if `PrintWindow` reports failure; that fallback captures whatever is
  currently visible at those screen coordinates, so it only reflects the target window's true
  content when the window is topmost/unobscured at capture time — a caller relying on the
  fallback path specifically should ensure the window is not covered by another window first.
- Success: `{ "success": true, "screenshotPath": "C:\\...\\shot_20260915...png" }`
- Failure: `invalid-argument`, `stale-context` (the resolved window closed before/during capture),
  `ambiguous-window`, `element-not-found` (same window-resolution errors as `inspect`), or
  `window-not-responding` (checked up front, matching `read-visible-text`).
- Screenshots save to the same folder as `inspect`'s screenshots:
  `%LOCALAPPDATA%\AgentDebugToolkit\screenshots\`.
- Validated live (2026-09-15) against the same Visual Studio Insiders window while it was fully
  obscured on-screen by another application window: the `PrintWindow` path correctly captured the
  target window's own content (menu bar, editor, Chat panel with correct text), confirming the
  fix for the `CopyFromScreen`-only fallback's obscured-window limitation.

## Conventions for future phases

- New verbs must follow the same JSON envelope style (`success`, verb-specific fields on success,
  `error`/`message` on failure).
- Document every new verb here before/as it's implemented — this file is the source of truth the
  agent relies on to issue commands correctly.

---

# CLI Contract — AgentDebugToolkit.Debugger.VisualStudio (Phase 6)

Commands are invoked as `agentdebug-vs <verb> [options]` and print one JSON object to stdout,
following the same envelope conventions as `agentdebug-ui` above (exit 0 = success, non-zero =
failure with `"error"`/`"message"` populated). This is a fully independent project/process from
`AgentDebugToolkit.UiAutomation.Cli` — no navigation-map awareness, no shared dependency beyond
JSON envelope *style* (see `docs/ARCHITECTURE.md`).

## Attaching to Visual Studio

Every verb accepts an optional `--solution <name>` (solution file name, without extension) to
pick a specific running `devenv.exe` instance when more than one is open. If omitted, the first
running instance found via the Running Object Table (ROT) is used.

- Failure (any verb, if no matching instance is found or the ROT can't be read):
  `{ "success": false, "error": "devenv-not-found" | "rot-unavailable", "message": "..." }`
- Failure (call requires break mode but the debugger isn't stopped):
  `{ "success": false, "error": "not-in-break-mode", "message": "..." }`
- Failure (call requires break mode and the debugger is stopped, but has no current
  thread/stack frame to inspect — e.g. between a break and the IDE fully settling):
  `{ "success": false, "error": "no-stack-frame", "message": "..." }`
- Failure (`continue` called with no active debugging session at all):
  `{ "success": false, "error": "no-active-session", "message": "..." }`
- Failure (any verb, if a COM call into Visual Studio kept failing with the IDE reporting itself
  busy — `RPC_E_SERVERCALL_RETRYLATER` / `RPC_E_CALL_REJECTED` — after automatic retries):
  `{ "success": false, "error": "com-busy-retry-exhausted", "message": "..." }`
  Every EnvDTE/COM call site in this project is wrapped in a shared retry helper (`ComRetry`)
  that retries up to 3 times with a short backoff (100ms/200ms/300ms) before surfacing this
  error, since Visual Studio's COM message filter can transiently reject cross-process calls
  while it's busy (e.g. mid-build, mid-launch, servicing another call).

## Verbs

### `debugger-status [--solution <name>]`
Reports the debugger's current mode and, if stopped at a breakpoint, the last-hit
file/line (via `Debugger.BreakpointLastHit`; `null` if break mode was entered some other way,
e.g. an unhandled exception or "Break All", where no single breakpoint object applies).
- Success: `{ "success": true, "mode": "design" | "run" | "break", "activeDocument": "C:\\...\\Foo.cs" | null, "activeLine": 42 | null }`
  `activeDocument`/`activeLine` are always present as explicit JSON `null` (not omitted) when
  not applicable — this verb opts into `preserveNullFields`, the same behavior documented for
  `wait-for-element` above, since callers need to distinguish "field present but null" from a
  malformed/older response.

### `get-callstack [--solution <name>]`
Returns the current thread's call stack. Requires break mode.
- Success: `{ "success": true, "frames": [ { "function": "Namespace.Type.Method" }, ... ] }`
  (ordered outermost-last, i.e. `frames[0]` is the innermost/current frame, per
  `EnvDTE.Debugger.CurrentThread.StackFrames` enumeration order.)
- Failure: `no-stack-frame` if the debugger has no current thread (see above).

### `get-locals [--solution <name>]`
Returns local variables for the current (innermost) stack frame. Requires break mode.
- Success: `{ "success": true, "locals": [ { "name": "x", "value": "5", "type": "int" }, ... ] }`
  (`value` is EnvDTE's string representation of the expression, not a typed/structured value.)
- Failure: `no-stack-frame` if the debugger has no current stack frame (see above).

### `get-exception-info [--solution <name>]`
Returns exception details if break mode was triggered by a thrown exception (detected via the
debugger's synthetic `$exception` pseudo-variable in the current frame's locals). Requires
break mode.
- Success: `{ "success": true, "exceptionType": "System.NullReferenceException", "message": "Object reference not set to an instance of an object." }`
- Failure: `{ "success": false, "error": "no-active-exception", "message": "Break mode was not triggered by an exception." }`
- Failure: `no-stack-frame` if the debugger has no current stack frame (see above).

### `continue [--solution <name>]`
Resumes execution (`Debugger.Go`). Requires an active debugging session (`CurrentMode` other
than `dbgDesignMode`) — `EnvDTE.Debugger.Go` is the same entry point as Start Debugging (F5), so
calling it with no active session would launch a brand-new debug session against the IDE's
current startup project rather than being a no-op; this verb explicitly rejects that case
instead of silently starting one.
- Success: `{ "success": true, "mode": "design" | "run" | "break" }`
- Failure: `{ "success": false, "error": "no-active-session", "message": "There is no active debugging session to resume." }`

### `step-over [--solution <name>]` / `step-into [--solution <name>]` / `step-out [--solution <name>]`
Steps one line, following EnvDTE's `Debugger.StepOver`/`StepInto`/`StepOut`. Requires break mode.
- Success: `{ "success": true, "mode": "design" | "run" | "break" }`
  (`mode` reflects the state immediately after the step call returns; a step can itself land back
  in break mode at the next line, or in run mode if it triggers further execution.)

### `start-debugging [--solution <name>]`
Starts a new debugging session (equivalent to pressing F5 / `Debug.Start`, launching the IDE's
configured startup project) via `dte.ExecuteCommand("Debug.Start", "")`. Only valid from design
mode — matches the same "guard against surprising side effects" principle as `continue`'s
`no-active-session` guard, just in the opposite direction: this verb refuses to restart or
interfere with a session that's already running/broken rather than silently doing nothing.
- Success: `{ "success": true, "mode": "design" | "run" | "break" }`
- Failure: `{ "success": false, "error": "already-debugging", "message": "A debugging session is already active (run or break mode)." }`

### `stop-debugging [--solution <name>]`
Stops the active debugging session via `Debugger.Stop(WaitForDesignMode: true)`. Requires an
active session (run or break mode); rejects the call from design mode with the same
`no-active-session` error code `continue` uses for its analogous guard.
- Success: `{ "success": true, "mode": "design" | "run" | "break" }`
- Failure: `{ "success": false, "error": "no-active-session", "message": "There is no active debugging session to stop." }`
- Failure: `no-stack-frame` if the debugger has no current stack frame to step from (see above).

---

# CLI Contract — AgentDebugToolkit.ConsoleAutomation.Cli (Phase 7)

Commands are invoked as `agentdebug-console <verb> [options]` and print one JSON object to
stdout. Exit code 0 means success; a non-zero exit code has a `"success": false` envelope with
`"error"` and `"message"` fields.

The CLI is a short-lived named-pipe client. `launch` starts a detached broker that owns the
ConPTY session, target process, terminal buffer, and pipe server; later commands reuse its
persisted session context. The persisted broker PID and UTC start time are validated before use,
so a stale or PID-reused session is removed and reported as `session-not-found`.

`read-screen` returns the ConPTY VT terminal buffer, not a native console-window capture. The
broker processes exactly one newline-terminated JSON request per pipe connection. It serializes
target input through a bounded single-writer queue; if that queue is full, input commands return
`input-busy` rather than blocking pipe handlers.

### `launch --exe <path> [--args <value>] [--cols <n>] [--rows <n>]`
Starts a detached broker and target under ConPTY. `--cols` and `--rows` default to `120` and
`30`; both must be positive. `--args` is preserved as one value, including when its value begins
with `--`.
- Success: `{ "success": true, "sessionId": "guid-without-hyphens", "pid": 12345 }`
- Failure: `invalid-argument`, `already-running`, `launch-in-progress`, `broker-unreachable`, or
  `session-write-failed`.

### `read-screen [--lines <n>]`
Returns all buffer rows, or the requested positive count of trailing rows.
- Success: `{ "success": true, "lines": ["C:\\path>command", "output", ...] }`
- Failure: `invalid-argument`, `session-not-found`, or `broker-unreachable`.

### `send-text --text <text>`
Queues text for UTF-8 input to the active ConPTY target.
- Success: `{ "success": true, "sent": true }`
- Failure: `invalid-argument`, `session-not-found`, `broker-unreachable`, `target-exited`, or
  `input-busy`.

### `send-keys --keys <keys>`
Queues a supported terminal key sequence. Supported keys are `ENTER`, `TAB`, `ESC`, `UP`, `DOWN`,
`LEFT`, `RIGHT`, and `CTRL+C`.
- Success: `{ "success": true, "sent": true }`
- Failure: `invalid-argument`, `session-not-found`, `broker-unreachable`, `target-exited`, or
  `input-busy`.

### `wait-for-text --pattern <text|regex> [--regex] [--timeoutMs <n>] [--pollMs <n>]`
Polls the terminal buffer until the literal pattern, or a .NET regular expression when `--regex`
is present, matches. `--timeoutMs` defaults to `5000`; `--pollMs` defaults to `250`; both must
be positive. Each pipe request, matching attempt, and sleep is bounded by the remaining deadline.
- Success: `{ "success": true, "matched": true, "elapsedMs": 210 }`
- Failure: `invalid-argument`, `session-not-found`, `broker-unreachable`, or `timeout`.

### `is-running`
Reports target liveness. Liveness uses the process wait handle rather than treating exit code 259
as a sentinel, so a target that really exits with code 259 is reported as stopped.
- Running: `{ "success": true, "running": true, "exitCode": null }`
- Exited: `{ "success": true, "running": false, "exitCode": 0 }`
- Failure: `session-not-found` or `broker-unreachable`.

### `stop`
Requests broker shutdown, which terminates the target if needed, drains final ConPTY output, and
clears the current persisted session context.
- Success: `{ "success": true, "stopped": true }`
- Failure: `session-not-found` or `broker-unreachable`.

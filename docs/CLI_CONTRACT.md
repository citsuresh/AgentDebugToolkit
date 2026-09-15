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
- `elementFound` reflects the element's state as resolved **before** the click is invoked, not
  after — this avoids reporting the click's own aftereffect (e.g. `isEnabled`/`isOffscreen`
  changing, or the element disappearing) as if it were the pre-click target. A caller that needs
  to confirm the click's effect (e.g. an element becoming disabled/offscreen, or a new window
  appearing) should use `wait-for-element`/`wait-for-window-change` afterward rather than reading
  this field.
- Failure: `element-not-found` if selector resolves to nothing.

### `type --hwnd <h> --strategy <s> --value <v> --text <input> [--scopeHwnd <h2>] [--verify]`
Resolves element, attempts `ValuePattern.SetValue`, falls back to click-to-focus + synthetic
keyboard input.
- Success: `{ "success": true, "method": "pattern" | "synthetic-keyboard" }`
- **`--verify` (optional, Phase 9, added 2026-09-15):** boolean-style flag matching `inspect`'s
  `--screenshot` convention — absent or `--verify false` disables it, any other value (including
  a bare `--verify`) enables it. After typing, re-reads the element's text (same mechanism as
  `get-text`) and compares it to `--text`. **Verification is only performed when `method` is
  `"pattern"`** (i.e. `ValuePattern.SetValue` was used) — when `Type` falls back to
  `"synthetic-keyboard"`, the corresponding read-back (`get-text`) also has no `ValuePattern` to
  read from and falls back to the element's accessibility `Name` (a static label, not typed
  content), which would produce a false-positive mismatch on essentially every successful type
  into such a control. `--verify` is silently skipped (not reported as an error) for
  `"synthetic-keyboard"` results — the response is the normal `{ "success": true, "method":
  "synthetic-keyboard" }` with no verification having occurred. Callers needing verified confirmation
  for synthetic-keyboard-only controls must currently do their own `get-text`-based comparison
  with a control-appropriate expectation (e.g. reading `Value` some other way), since generic
  `Name`-based comparison cannot serve that purpose.
- Failure (verify only): `verify-mismatch` if `method == "pattern"` and the read-back text does
  not equal `--text`. Response includes `expected`/`actual` fields:
  `{ "success": false, "error": "verify-mismatch", "message": "...", "expected": "...", "actual": "..." }`.
  Found and fixed during Regression Audit: an initial version used presence-only detection
  (`opts.ContainsKey("verify")`), which diverged from `--screenshot`'s convention (no way to pass
  `--verify false` to disable it) — fixed to match. The audit also found the pattern-only
  verification scoping described above was necessary to avoid the `synthetic-keyboard` false-positive
  described above; this was fixed before any live validation, not discovered live.
- Validated live (2026-09-15) against the real Copilot Chat input (`--strategy Name --value "Ask
  Copilot"`, resolved via the synthetic-keyboard fallback since this control has no `ValuePattern`):
  `type --verify` returned `{ "method": "synthetic-keyboard", "success": true }` with no
  `verify-mismatch` — confirming verification was correctly skipped rather than spuriously
  failing. The `verify-mismatch` failure path itself (for `ValuePattern`-backed controls) was
  validated by code review and build only, not live, per user direction (no safe
  `ValuePattern`-backed control was available to deliberately mistype into in this session).
- **Known limitation — embedded newlines are rejected, even for `ValuePattern`-backed controls
  (added 2026-09-15).** `--text` containing an embedded `\n`/`\r` is rejected up front with
  `invalid-argument`, before window/element resolution: `{ "success": false, "error":
  "invalid-argument", "message": "--text must not contain embedded newline characters..." }`. This
  is a real behavior change (not just a doc note) — previously a raw newline was passed through
  uninterpreted-as-literal to the underlying `SendKeys.SendWait` call on the synthetic-keyboard
  fallback path, which was observed live to trigger unintended UI navigation (unexpectedly
  focusing a different control) rather than being typed as literal text. The rejection is
  unconditional across **both** of `type`'s paths, including `ValuePattern.SetValue` (the
  `"pattern"` method), even though that path does not go through `SendKeys` and could not exhibit
  the observed navigation bug — a multi-line `ValuePattern`-backed control therefore cannot
  currently receive newline text via `type` at all. This was a deliberate simplicity/uniformity
  tradeoff (one consistent rule across both paths) rather than an oversight — flagged during
  Regression Audit and confirmed as the intended tradeoff rather than fixed to be path-aware.
  Validated live (2026-09-15): `type --text "line1\nline2"` (containing an embedded newline)
  against the Copilot Chat input returned a clean `invalid-argument` with no unhandled exception
  and no attempt to send the text.

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

## Phase 9 verbs (reliable interaction primitives)

### `activate --hwnd <h>`
Brings the given window to the foreground via `SetForegroundWindow`. **Unlike every Phase 8
verb, this is intentionally interactive/non-read-only** — it changes window activation, z-order,
and input focus rather than only observing the target. Requires an explicit `--hwnd`; does not
fall back to `--pid`/persisted session context.
- Success: `{ "success": true, "activated": true }`
- Failure: `invalid-argument` if `--hwnd` is missing or malformed.
- Failure: `stale-context` if `SetForegroundWindow` returns false. This can mean the window
  closed, **or** that Windows legitimately denied the foreground switch due to focus-stealing
  prevention (which process last had input focus determines whether the OS honors the request) —
  this is not necessarily a fatal condition for the caller. A caller receiving `stale-context`
  from `activate` should not assume the window is gone; it may retry, or fall back to manual
  activation (e.g. asking the user to click the window) before continuing with `click`/`type`.
- Validated live (2026-09-15): backgrounded the target Visual Studio Insiders window (hwnd
  `0xCA18B2`) by opening Notepad on top of it, confirmed via `list-windows` that its
  `isForeground` was `false`, then called `activate --hwnd 0xCA18B2` from a shell that was itself
  not the foreground process. `SetForegroundWindow` returned `false` and the verb correctly
  reported `stale-context` with the documented message; a follow-up `list-windows` confirmed the
  window remained backgrounded. This is a live demonstration of the documented focus-stealing
  prevention behavior itself (the calling process was not privileged to steal foreground focus),
  not a verb defect — it confirms the failure path is surfaced cleanly rather than crashing or
  silently no-oping. Also validated the `invalid-argument` paths: missing `--hwnd`, empty string,
  `"0x"`, and non-hex text all return a clean `invalid-argument` response (no unhandled
  exception), including the empty/`"0x"` cases that required broadening the exception filter to
  catch `ArgumentException` (found via Regression Audit, see below).
- **Known limitation — foreground denial depends on the caller's own focus state, not just the
  target's.** `SetForegroundWindow` is denied by Windows' focus-stealing prevention whenever the
  *calling* process does not itself currently hold foreground/input focus — this was reproduced
  live (2026-09-15): a shell that was not itself the foreground process called `activate` against
  a valid, open, backgrounded window and was denied (`stale-context`), even though the target
  window was perfectly healthy. This means a caller cannot assume `activate` will succeed just
  because the target window is known-good; success also depends on what currently holds
  foreground focus on the desktop at call time, which the caller does not directly control. A
  `stale-context` result from `activate` should be treated as "could not activate this time," not
  as evidence the window/session is invalid.

### `send-keys --hwnd <h> --strategy <s> --value <v> --keys <SendKeys syntax>`
Companion to `type` for input `type` cannot express. `type`'s underlying `SendText` always
escapes `SendKeys` special characters (`+^%~(){}[]`) so literal input text is never
misinterpreted as `SendKeys` syntax — this means `type` has no way to send key combinations like
`Ctrl+A`, `Delete`, or `Enter` as actual key presses. `send-keys` instead accepts and passes
through **unescaped** `SendKeys.SendWait` syntax via `--keys` (e.g. `--keys "^a"` for Ctrl+A,
`--keys "{DELETE}"`, `--keys "{ENTER}"`). Element-scoped, consistent with `click`/`type`: resolves
the window and element via the same `--hwnd`/`--strategy`/`--value` selector mechanism, checks
`IsResponding` up front, then click-to-focuses the element (same as `type`'s fallback path)
before sending the raw key sequence. There is no UIA-pattern fast path (unlike `type`'s
`ValuePattern` attempt) since there is no pattern equivalent for raw key-combination input —
`send-keys` always uses synthetic keyboard input.
- Success: `{ "success": true, "sent": true }`
- Failure: `invalid-argument` if `--keys` is missing/empty, or if `--keys` is not valid `SendKeys`
  syntax (e.g. an unbalanced `{` or an unrecognized key name like `{FOO}` — `SendKeys.SendWait`
  throws for these; `send-keys` catches this and reports it as `invalid-argument` with the
  underlying message rather than propagating as `unhandled-exception`).
- Failure: same window/element-resolution error codes as `click`/`type`
  (`element-not-found`/`ambiguous-window`/`stale-context`/etc.), and `window-not-responding`
  (checked up front, matching `click`/`type`).
- **Known limitation — embedded newlines.** Like `type` (see below), `--keys` is not restricted
  from containing characters that `SendKeys` syntax interprets specially in ways that don't map
  to "literal key" (e.g. a `{FOO}` typo, or characters requiring escaping that were not escaped).
  Since `--keys` is documented as raw/unescaped by design, this is expected — callers are
  responsible for passing valid `SendKeys` syntax; `send-keys` only guards against outright
  `SendKeys.SendWait` exceptions, not semantically "wrong but syntactically valid" key sequences
  (e.g. sending `{ENTER}` to a control that doesn't expect it).
- Validated live (2026-09-15) against the real Visual Studio Insiders window's Copilot Chat
  input box (resolved via `--strategy Name --value "Ask Copilot"`, the placeholder Name shown
  when the input is empty — note `--strategy AutomationId --value WpfTextView` was tried first
  and resolved ambiguously to the code editor pane instead, since `WpfTextView` is not unique to
  the chat input; `Name`-based selection was used instead for this specific control). Sent
  literal-safe `--keys "test"`, confirmed via `read-visible-text` that "test" landed in the chat
  input, then cleared it back to empty via `--keys "^a{DEL}"` (Ctrl+A, Delete), confirmed via a
  follow-up screenshot showing the input back at its placeholder-empty state. Also validated that
  malformed syntax (`--keys "{"`) returns a clean `invalid-argument` rather than crashing.

### `submit-chat-message --hwnd <h> (--inputAutomationId <id> | --inputStrategy Name --inputValue <value>) [--sendAutomationId <id> | --submitKeys <SendKeys syntax>] --text <input>`
Composite verb tailored to the Copilot Chat input pattern specifically: click the input, type the
text, verify it landed via read-back, then submit it — as a single call instead of a caller
scripting the equivalent `click` → `type --verify` → `click`/`send-keys` sequence themselves.
Unlike `click`/`type`/`send-keys` (which accept any `Selector` strategy via `--strategy`/`--value`),
this verb's input selection is limited to two explicit modes (see below), and submission is either
an `AutomationId`-based Send-button click or keyboard input to the input element.
- **Input selection (Phase 10, 2026-09-15):**
  - `--inputAutomationId <id>` — original compatibility mode: resolve the input by `AutomationId`.
  - `--inputStrategy Name --inputValue <value>` — resolve the input by its `Name` instead. Added
    specifically for the real Visual Studio Copilot Chat composer, which exposes no
    `AutomationId` distinguishing it from the code editor's `WpfTextView`/`AutomationId` but does
    expose a unique `Name` ("Ask Copilot") while its text is empty.
  - Exactly one of these two modes must be specified; specifying both, or neither in a valid form,
    is `invalid-argument`.
- **Submission (Phase 10, 2026-09-15):**
  - `--sendAutomationId <id>` — original compatibility mode: resolve a Send button by
    `AutomationId` and click it via `UiaHelper.Click`.
  - `--submitKeys <SendKeys syntax>` — send raw/unescaped `SendKeys` syntax to the already-resolved
    input element via `UiaHelper.SendKeys` (same mechanism as the standalone `send-keys` verb)
    instead of resolving a separate Send button. If neither `--sendAutomationId` nor
    `--submitKeys` is given, this is the default path, using `{ENTER}`.
  - `--sendAutomationId` and `--submitKeys` are mutually exclusive.
- Internal sequence: resolve window → resolve input element (by `AutomationId` or `Name`) →
  synthetic click at the input's bounding-rect center (see rationale below — deliberately NOT
  `UiaHelper.Click`) → `UiaHelper.Type` → conditional read-back verification (only when `method ==
  "pattern"`, same rationale as `type --verify`) → either resolve Send button by
  `--sendAutomationId` and `UiaHelper.Click` it, or `UiaHelper.SendKeys` the input with
  `--submitKeys` (default `{ENTER}`).
- Success: `{ "success": true, "method": "pattern" | "synthetic-keyboard" }` (the `method` reflects
  how the input's `Type` call proceeded; the Send click/keyboard submission's own internal
  mechanism is not reported — a known simplification, see below).
- Failure: `invalid-argument` (with `"step": "validate-arguments"`) for a missing/empty `--text`;
  an embedded newline in `--text` (same rejection as `type`, duplicated here since this verb calls
  `UiaHelper.Type` directly rather than going through `Verbs.Type`); a missing, empty, or
  conflicting input-selection mode (`--inputAutomationId` together with
  `--inputStrategy`/`--inputValue`, or neither a valid `--inputAutomationId` nor a valid
  `--inputStrategy Name --inputValue <value>` pair); an empty `--sendAutomationId` or
  `--submitKeys`; or `--sendAutomationId` together with `--submitKeys`.
- Failure: any window-resolution error code (with `"step": "resolve-window"`), or
  `window-not-responding` (with `"step": "resolve-window"`).
- Failure: `element-not-found` (with `"step": "resolve-input"`) if the selected input
  (`AutomationId` or `Name`) does not resolve.
- Failure: `verify-mismatch` (with `"step": "type-verify"`, plus `expected`/`actual` fields) if the
  input element supports `ValuePattern` (`method == "pattern"`) and the read-back text after typing
  does not match `--text`. **Unlike `type --verify` (which is opt-in), this verb always attempts
  verification when possible** — it exists specifically to catch silent typing failures before
  committing to submitting.
- Failure: `element-not-found` (with `"step": "resolve-send"`) if an explicitly requested Send
  button `AutomationId` does not resolve. **Note:** if this occurs after typing has already
  succeeded (and passed verification, if attempted), the typed text remains in the input — it is
  not cleared or rolled back. A caller retrying after a `resolve-send` failure should account for
  the input already containing the previously-typed text.
- Failure: `invalid-argument` (with `"step": "submit"`) if `--submitKeys` (or the default
  `{ENTER}`) is not valid `SendKeys` syntax — same translation as the standalone `send-keys` verb
  (`ArgumentException`/`InvalidOperationException`/`FormatException` from `SendKeys.SendWait`).
  Same partial-typed-state caveat as a `resolve-send` failure: the typed text remains in the input.
- **Design note — synthetic click instead of `UiaHelper.Click` for the input.** The input-focus
  click (before typing) uses a plain `NativeMethods.Click(x, y)` synthetic mouse click at the
  element's bounding-rect center, not `UiaHelper.Click`. This was a fix applied during Regression
  Audit: `UiaHelper.Click` tries `InvokePattern`/`TogglePattern` before falling back to a synthetic
  click, so reusing it here risked invoking or toggling the input element as an unintended side
  effect if it happened to also expose one of those patterns — a real risk distinct from `Type`'s
  own click-to-focus fallback, which is always a plain physical click. The Send button click still
  uses `UiaHelper.Click` (an actual invoke/click of the button is the intended action there).
- **Known limitation — partial-typed state on `verify-mismatch`.** If verification fails, the
  mismatched/partial text remains in the input and submission never happens, leaving the input in
  a different state than before the call. This is inherent to a composite verb that performs real,
  non-transactional side effects across multiple steps — there is no rollback.
- **Known limitation — no ambiguity detection on `AutomationId`/`Name` lookups.** Input and Send
  button resolution use the existing `ResolveSelector` helper, which returns the first match
  (`FindFirst`) with no error if multiple elements share the same `AutomationId`/`Name` within the
  window — this is a pre-existing limitation of `ResolveSelector` shared with `click`/`type`, not
  something new to this verb, but is worth calling out here since this verb can chain two such
  lookups back to back (input + Send button, when `--sendAutomationId` is used).
- **Live validation status (2026-09-15, Phase 9): not completed end-to-end against a real Copilot
  Chat panel using the original `AutomationId`-only design.** A full tree inspection
  (`inspect --hwnd 0xCA18B2 --maxDepth 15`) of the real, running VS Insiders window found no
  `AutomationId` resembling a Send button anywhere in the tree, and the only `AutomationId`
  matching `WpfTextView` resolves to the code editor pane, not the chat input — consistent with
  `send-keys`'s finding that the chat input required a `Name`-based selector instead. This gap is
  what Phase 10's `--inputStrategy Name`/`--submitKeys` modes above were added to close.
- **Live validation status (2026-09-15, Phase 10): completed end-to-end.** Against this session's
  own VS Insiders window (hwnd `0xCA18B2`), `submit-chat-message --hwnd 0xCA18B2 --inputStrategy
  Name --inputValue "Ask Copilot" --text "ping-test-phase10"` resolved the composer by `Name`,
  typed the text (`method: "synthetic-keyboard"`), and submitted it via the default `--submitKeys
  {ENTER}` — result `{"method":"synthetic-keyboard","success":true}`, with the message actually
  landing in and being processed by the live chat session. No `--sendAutomationId`/Send-button
  resolution was needed for this path.

### `has-pending-prompt --hwnd <h>` (Phase 11, 2026-09-15)
Lightweight, shallow-cost check for whether a Copilot Chat-style confirmation prompt (a tool
approval/question card with Submit/Cancel and optionally radio-button options, or a freeform
question with a text field) is currently blocking on user input — without walking/serializing the
entire accessibility tree the way `inspect --maxDepth N` does. Intended for repeated polling while
waiting for an agent turn to either finish or need input, where a deep `inspect` call is too
costly to run on every poll.
- **Detection anchor.** A pending confirmation is identified by the existence of a descendant
  element with `Name == "Waiting..."` anywhere under the resolved window — the only anchor
  validated so far for this UI (see `tools/Watch-CopilotChat.ps1`, the PowerShell polling script
  this verb supersedes for the "is something pending" check specifically). Resolution uses a
  single `FindFirst` (via the existing `Name` `Selector` strategy), which stops at the first match
  and never serializes visited nodes — this is why it is meaningfully cheaper than a
  depth-bounded `inspect`, which still walks and serializes every node up to that depth regardless
  of whether anything relevant is found. No shallower/cheaper anchor has been identified.
- When a pending prompt is found, the question text is read from descendant(s) with
  `AutomationId == "RadioFieldLabel"` (joined with `" | "` if more than one), and its options from
  `ControlType.RadioButton` descendants, excluding the literal `"Other"` label (a generic fallback
  option, not real content). A freeform (non-radio) prompt — e.g. a text-field confirmation — has
  no such nodes, so `question` and `options` are simply empty in that case; this is not an error.
- Success (not pending): `{ "success": true, "pending": false }`. This is the cheap/fast path —
  no `RadioFieldLabel`/`RadioButton` lookups are attempted when `"Waiting..."` is not found.
- Success (pending): `{ "success": true, "pending": true, "question": "<joined text>", "options": [...] }`.
- Failure: same window-resolution error codes as `inspect` (`element-not-found`/
  `ambiguous-window`/`stale-context`/etc.), and `window-not-responding` (checked up front, matching
  `click`/`type`/`send-keys`/`submit-chat-message`).
- Unlike `inspect`, this verb has no `--maxDepth` option — it is intentionally not
  depth-configurable, since its detection is anchor-based (`FindFirst`) rather than a bounded tree
  walk.
- **Regression Audit finding — degrade gracefully on tree mutation mid-poll.** The
  `RadioFieldLabel`/`RadioButton` `FindAll` lookups are wrapped in try/catch: since this verb is
  intended for repeated polling against a live, actively-changing chat panel, the confirmation
  card can be dismissed/replaced between the initial `"Waiting..."` match and these follow-up
  lookups. A transient UIA exception here degrades to an empty `question`/`options` (same as the
  freeform-prompt case) rather than surfacing as `unhandled-exception`.
- **Live validation (2026-09-15):** benchmarked directly against this session's own VS Insiders
  window (hwnd `0xCA18B2`, `DOTNET_ROOT` cleared to avoid the VS-inherited-runtime launch issue):
  `has-pending-prompt` returned in ~1.4s in the not-pending state versus ~5.8s for
  `inspect --hwnd 0xCA18B2 --maxDepth 20` (~4.2x faster). Both branches were validated against
  real UI state: `{"pending":false,"success":true}` while idle, and
  `{"pending":true,"question":"","options":[],"success":true}` while a real (freeform,
  non-radio-button) confirmation card was live on screen — the empty `question`/`options` in that
  result is expected per the freeform-prompt case documented above, not a detection failure (the
  `"Waiting..."` anchor itself was still found correctly, which is what `pending: true` reflects).
  The radio-button `question`/`options` population path (`RadioFieldLabel`/`RadioButton` lookups)
  reuses the same `PropertyCondition` patterns already validated by `tools/Watch-CopilotChat.ps1`
  in earlier sessions, but was not independently re-validated live against a radio-button-style
  card in this session — no such card happened to appear during validation.

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

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
- `NameRegex`: value is a regex tested against `Name`. **Not yet implemented** —
  `Enum.TryParse<SelectorStrategy>` accepts it as a valid `--strategy` value, but
  `UiaHelper.ResolveSelector`/`ResolveSelectorAll` throw `NotSupportedException` for it (see
  `docs/KNOWN_OPEN_FINDINGS.md`).
- `ControlTypeIndex`: value format `"<ControlType>:<index>"`, e.g. `"Button:2"` — the nth matching
  control (0-based) among descendants of the scope. **Not yet implemented** (same as `NameRegex`
  above).
- `Coordinates`: value format `"x,y"`, **client-area-relative** to the scope window. **Not yet
  implemented** (same as `NameRegex` above).

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
- **Fixed 2026-09-15:** a trailing `.exe` suffix on `--process` (e.g. `--process Fdm.exe`) is now
  stripped (case-insensitively) before matching against `Process.ProcessName` (which never includes
  it). Previously this silently produced `process-not-found` despite the "match flexibly" contract
  above — the flexible matching was documented but not actually implemented for the `.exe` case.
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
- **Fixed 2026-09-15:** `--scopeHwnd` is now actually honored — previously it was accepted as an
  argument but silently ignored, and the selector was always resolved against `--hwnd`/the session
  context window regardless. It now narrows the selector search to the element identified by
  `--scopeHwnd` (and its subtree) when given, falling back to `--hwnd` otherwise, matching the
  documented contract. Applies to both `click` and `type` (both call the same internal resolution
  helper). An invalid or unresolvable `--scopeHwnd` fails with `invalid-argument` /
  `element-not-found` respectively, before the `--strategy`/`--value` selector is even attempted.
- Success: `{ "success": true, "method": "pattern" | "synthetic-click", "elementFound": ElementInfo }`
- `elementFound` reflects the element's state as resolved **before** the click is invoked, not
  after — this avoids reporting the click's own aftereffect (e.g. `isEnabled`/`isOffscreen`
  changing, or the element disappearing) as if it were the pre-click target. A caller that needs
  to confirm the click's effect (e.g. an element becoming disabled/offscreen, or a new window
  appearing) should use `wait-for-element`/`wait-for-window-change` afterward rather than reading
  this field.
- Failure: `element-not-found` if selector resolves to nothing.

### `type --hwnd <h> --strategy <s> --value <v> --text <input> [--scopeHwnd <h2>] [--verify] [--paste]`
Resolves element, attempts `ValuePattern.SetValue`, falls back to click-to-focus + synthetic
keyboard input.
- Success: `{ "success": true, "method": "pattern" | "synthetic-keyboard" | "clipboard-paste" }`
  (`clipboardRestored: true|false|null` is also included when `method == "clipboard-paste"` — see
  the tri-state explanation under `--paste` below).
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
- **Known limitation / mitigation — asynchronous post-paste mutation by the target control
  (Phase 14, added 2026-09-15).** A real bug was observed pasting multi-paragraph text (with
  blank-line paragraph breaks) into a JS-driven chat composer: `type --paste --verify` reported
  success (`method: "clipboard-paste"`, no `verify-mismatch`), but the composer itself then
  asynchronously truncated the content to just the first line and/or submitted it — after our
  one-shot read-back already looked complete. Root cause: some composer-style targets (unlike
  native Win32 edit controls) treat an embedded newline as a submit trigger or otherwise mutate
  pasted content on a delay, racing ahead of a single synchronous verification read. This is
  app-side behavior on the target control, not a defect in the paste/verify mechanism itself, and
  cannot be fully eliminated from the automation side. Mitigation implemented: for
  `method == "clipboard-paste"` only, after the existing exact-match verification passes, a
  second check (`VerifyPasteStability`) (a) flags the read-back as suspicious if its length is
  under 50% of the input length even though it nominally matched, and (b) re-reads the element
  after an additional ~250ms settle delay and compares against the first read, failing with a new
  `verify-unstable` error (`{ "success": false, "error": "verify-unstable", "message": "...",
  "expected": "...", "actual": "..." }`) if the content changed or shrank in that window, instead
  of reporting success. This does not apply to `method == "pattern"` (`SetValue` is synchronous;
  no such race exists) or `"synthetic-keyboard"` (verification is already skipped for that path).
  Callers should treat `verify-unstable` as a signal to retry, reduce message size, or investigate
  the target control's own paste-handling behavior — it is not a transient/no-action error.
- Validated live (2026-09-15) against the real Copilot Chat input (`--strategy Name --value "Ask
  Copilot"`, resolved via the synthetic-keyboard fallback since this control has no `ValuePattern`):
  `type --verify` returned `{ "method": "synthetic-keyboard", "success": true }` with no
  `verify-mismatch` — confirming verification was correctly skipped rather than spuriously
  failing. The `verify-mismatch` failure path itself (for `ValuePattern`-backed controls) was
  validated by code review and build only, not live, per user direction (no safe
  `ValuePattern`-backed control was available to deliberately mistype into in this session).
- **Known limitation — embedded newlines are rejected on the synthetic-keyboard path only
  (added 2026-09-15; narrowed 2026-09-15).** `--text` containing an embedded `\n`/`\r` is rejected
  with `invalid-argument` — but only when the resolved element has no usable `ValuePattern` (i.e.
  `type` would otherwise fall back to the synthetic-keyboard path) and `--paste` is not given: `{
  "success": false, "error": "invalid-argument", "message": "--text must not contain embedded
  newline characters..." }`. The check now runs after window/element resolution rather than up
  front, since it needs to know which path the resolved element will take. This is a real behavior
  change (not just a doc note) — previously a raw newline was passed through uninterpreted-as-
  literal to the underlying `SendKeys.SendWait` call on the synthetic-keyboard fallback path, which
  was observed live to trigger unintended UI navigation (unexpectedly focusing a different control)
  rather than being typed as literal text.
  `ValuePattern`-backed controls (the `"pattern"` method, via `ValuePattern.SetValue`) are now
  **exempt** from this rejection and may receive embedded newlines directly: `SetValue` never goes
  through `SendKeys`, so the observed navigation bug does not apply there. This was originally a
  deliberate simplicity/uniformity tradeoff (one consistent rule across both paths, flagged during
  Regression Audit and confirmed as intended at the time) but was revisited once `--paste` existed
  as a proven safe multi-line path, and the unconditional rejection was judged an unnecessary
  restriction for `ValuePattern`-backed controls specifically — reconsidered and narrowed to be
  path-aware.
  Validated live (2026-09-15, before narrowing): `type --text "line1\nline2"` (containing an
  embedded newline) against the Copilot Chat input (synthetic-keyboard path) returned a clean
  `invalid-argument` with no unhandled exception and no attempt to send the text. The narrowed,
  path-aware behavior (`ValuePattern`-backed controls now accepting newlines via `"pattern"`) was
  validated by code review and build only, not live, pending a suitable multi-line
  `ValuePattern`-backed control to test against.
- **`--paste` (optional, Phase 12, added 2026-09-15):** boolean-style flag (same convention as
  `--verify`) that, when the target element has no usable `ValuePattern` (the case `--paste` is
  actually for), pastes `--text` via the clipboard (`Clipboard.SetDataObject` + `Ctrl+V`) instead
  of per-character synthetic keystrokes — faster and non-interruptible for long text, and unlike
  the plain synthetic-keyboard path, does **not** reject embedded newlines (pasted text is never
  typed character-by-character through `SendKeys.SendWait`, which is what made raw newlines
  dangerous on that path). Reports `method: "clipboard-paste"`.
  - **No-op when `ValuePattern` is available:** `--paste` is ignored and `method: "pattern"` is
    used instead — `ValuePattern.SetValue` is already instant and non-interruptible, so clipboard
    paste has nothing to improve there. The flag is not silently dropped from the response in this
    case; the reported `method` simply reflects what was actually used.
  - **Clipboard History / Cloud Clipboard opt-out:** the text written to the clipboard for a paste
    is marked with the documented Windows opt-out formats (`ExcludeClipboardContentFromMonitorProcessing`
    zero-byte marker, plus `CanIncludeInClipboardHistory`/`CanUploadToCloudClipboard` DWORD-0
    formats) so this transient automation input does not appear in Windows Clipboard History or
    sync via Cloud Clipboard. The restored original clipboard content (see below) is written
    plainly, without these markers, since it is the user's own prior data being put back, not new
    content from this operation.
  - **Original clipboard preserved (full fidelity):** before pasting, the current clipboard
    content is snapshotted via `Clipboard.GetDataObject()` (not text-only) and restored afterward
    via `Clipboard.SetDataObject(originalData, copy: true)` on a best-effort basis — preserving
    whatever formats were actually present (e.g. HTML/RTF/image/file-drop data alongside a text
    representation), not just a plain-text approximation. **Fixed during Regression Audit**: an
    earlier version snapshotted/restored via `GetText()`/`SetText()` only, which would have
    silently discarded any non-text formats that coexisted with text on the clipboard (a common
    case, e.g. copying a spreadsheet cell), permanently losing the user's actual prior clipboard
    content while still reporting a successful restore — fixed before commit, not discovered live.

    A second, related bug was found (and fixed) during live re-validation of the fix above:
    `Clipboard.GetDataObject()` returns a live COM wrapper tied to the current clipboard owner,
    not a deep copy of the data — once the clipboard is overwritten with the paste text, that
    wrapper can go stale, so restoring it back silently no-ops despite reporting
    `clipboardRestored: true`. Fixed by eagerly copying every format's actual data out of the
    snapshot into a `DataObject` the code owns (via `GetFormats()`/`GetData(format)`) before
    overwriting the clipboard, so the later restore writes real captured bytes rather than a
    now-invalid live reference. Confirmed via a manual marker-text round-trip against the real
    VS Copilot Chat composer (set a known clipboard marker, paste, confirm the marker was
    genuinely back on the clipboard afterward — not just a truthy flag).    `clipboardRestored` in the response is **tri-state**, not boolean, reflecting this fix:
    - `null` — there was nothing on the clipboard to restore (no prior content existed); not a
      failure, simply a no-op.
    - `true` — a prior clipboard snapshot existed and was successfully restored.
    - `false` — a prior clipboard snapshot existed but restoring it failed (e.g. the clipboard
      became locked by another process during the restore attempt); non-fatal — the paste itself
      already succeeded by this point — reported for visibility only.
  - **Clipboard busy/locked:** both the snapshot read and every clipboard write retry up to 3 times
    with a short backoff on a transient `ExternalException` (the standard Windows
    `CLIPBRD_E_CANT_OPEN` failure mode when another process holds the clipboard open). If writing
    still fails after retrying, returns `{ "success": false, "error": "clipboard-unavailable",
    "message": "..." }` rather than proceeding with a corrupted paste. A snapshot-read failure is
    non-fatal and simply skips the restore (`clipboardRestored: null`, same as "nothing to
    restore" — a read failure is treated the same as there having been no prior content to
    restore) instead of failing the whole call.
  - **Verification (`--verify` combined with `--paste`):** attempted for `method ==
    "clipboard-paste"` using a dedicated read-back (`GetTextForPasteVerification`) that tries
    `TextPattern` before falling back to `Name` — plain `GetText`'s `Name`-only fallback (used for
    `"synthetic-keyboard"`, where verification is still skipped) would almost always report a
    false-positive mismatch for a paste target, since `--paste` specifically targets elements
    without `ValuePattern`. Comparison normalizes line endings (`\r\n`/`\r`/`\n` all treated as
    equivalent) before comparing — live validation against a real `RichEdit`-based control showed
    the control itself normalizes `\n` to `\r` on paste, a genuine editor behavior rather than data
    loss, so a raw string comparison would report a false `verify-mismatch` for any multi-line
    paste.
  - **STA requirement — found live, not anticipated.** `System.Windows.Forms.Clipboard` requires
    the calling thread to be STA (`InvalidOperationException` otherwise: "Current thread must be
    set to single thread apartment (STA) mode..."). Top-level-statement `Main` does **not**
    automatically apply `[STAThread]` (a wrong assumption made during design, corrected by live
    testing) — the process now explicitly checks `Thread.CurrentThread.GetApartmentState()` at
    startup and, if not already STA, re-runs on a new thread with `SetApartmentState(STA)`. This
    is a startup-level fix (`Program.cs`'s entry point), not scoped to `--paste` alone, but was
    only exercised/discovered by `--paste`'s live validation since no prior verb used
    `System.Windows.Forms.Clipboard`.
  - Validated live (2026-09-15) against the real Copilot Chat composer (`--strategy Name --value
    "Ask Copilot"`, hwnd `0xCA18B2`, no submission — cleared via `send-keys --keys "^a{DEL}"`
    afterward, never pressing Enter): `type --paste true --verify true` with a 3-line
    `--text` containing embedded newlines returned `{"method":"clipboard-paste",
    "clipboardRestored":true,"success":true}` with no `verify-mismatch`, confirming the paste,
    verification, and clipboard-restore paths all worked correctly against the actual motivating
    target (a control with no `ValuePattern`).

### `get-text --hwnd <h> --strategy <s> --value <v>`
Reads current `Name` or `ValuePattern.Value` (whichever is more appropriate/available) of the
resolved element.
- Success: `{ "success": true, "text": "10.176.100.248" }`

---

## Phase 2 verbs

**Implementation status (added 2026-09-15): all four verbs below were previously documented but
had no dispatch case in the CLI (an unimplemented gap); they are now implemented as described.**
All four accept `--pid` explicitly or fall back to the persisted session context (same convention
as `list-windows`/`click`/`type`), even though the signatures below show `--pid` without brackets.

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
Polls the target process's foreground window with `SendMessageTimeout` (via
`NativeMethods.IsResponding`, 100ms probe) until it responds, or `timeoutMs` elapses — this
reflects whether the process's *main*/active window is responsive, not just any window (a
secondary/tool window could stay responsive while the main window hangs). Falls back to "any
top-level window responds" only when there is no single unambiguous window to prefer (no
foreground window and more than one top-level window); a process with exactly one window uses
that window regardless of its foreground state. A process with no top-level windows is treated as
not-responding.
- Success: `{ "success": true, "responding": true, "elapsedMs": 15 }`
- Failure: `"process-not-responding"` if it never responds within timeout; `invalid-argument` for a
  missing/negative `--timeoutMs`; same pid-resolution errors as other pid-based verbs otherwise.

### `delay --ms <n>`
Plain `Thread.Sleep`; reports actual elapsed time (may exceed the requested `--ms` slightly due to
OS scheduling granularity).
- Success: `{ "success": true, "waitedMs": 500 }`
- Failure: `invalid-argument` for a missing/negative `--ms`.

### `set-context --pid <n>`
Manual session-context override: persists `--pid` as the current process the same way `attach`
does, without a by-name lookup — useful when the caller already knows the pid.
- Success: `{ "success": true, "pid": 145376 }`
- Failure: `invalid-argument` for a missing/non-integer `--pid`; `stale-context` if the pid is not a
  running process.

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
- **Known limitation — `PrintWindow`/`PW_RENDERFULLCONTENT` capture can be incomplete or
  incorrectly scaled for a non-foreground/non-visible window (flagged during Phase 9, documented
  2026-09-15).** The obscured-but-still-visible-on-a-monitor case above was validated live and
  works correctly, but `PrintWindow` with `PW_RENDERFULLCONTENT` is not guaranteed to fully or
  correctly render a window that is minimized, on a different virtual desktop, or otherwise not
  currently part of the visible desktop composition — some applications (particularly
  DirectComposition/hardware-accelerated-rendering windows) can return a partially-rendered,
  stale, or incorrectly-scaled bitmap in that situation rather than failing outright, which would
  not be caught by the existing failure-triggered fallback to `CopyFromScreen` (since `PrintWindow`
  reports success even though the content is wrong). This was flagged as a concern during Phase 9
  but never live-validated against an actually-minimized or off-desktop window, and no code change
  has been made for it. Callers needing a guaranteed-correct capture should ensure the target
  window is not minimized and is on the current virtual desktop before calling `screenshot`.

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

### `submit-chat-message --hwnd <h> (--inputAutomationId <id> | --inputStrategy Name --inputValue <value>) [--sendAutomationId <id> | --submitKeys <SendKeys syntax>] --text <input> [--paste]`
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
  `UiaHelper.Click`) → `UiaHelper.Type` (or `UiaHelper.TypeViaPaste` if `--paste` is given and the
  input has no usable `ValuePattern`) → conditional read-back verification (`method == "pattern"`
  or `method == "clipboard-paste"`, same rationale as `type --verify`) → either resolve Send
  button by `--sendAutomationId` and `UiaHelper.Click` it, or `UiaHelper.SendKeys` the input with
  `--submitKeys` (default `{ENTER}`).
- **`--paste` (optional, Phase 12, added 2026-09-15):** same flag, mechanics, STA fix,
  Clipboard-History/Cloud-Clipboard opt-out, restore behavior, and line-ending-normalized
  verification as `type --paste` (see that section above) — this verb reuses
  `UiaHelper.TypeViaPaste` and `UiaHelper.GetTextForPasteVerification` directly rather than
  duplicating the logic. Also lifts the embedded-newline rejection for this verb's `--text` when
  `--paste` is used, for the same reason.
- Success: `{ "success": true, "method": "pattern" | "synthetic-keyboard" | "clipboard-paste" }`
  (`clipboardRestored: true|false|null` also included when `method == "clipboard-paste"` — see the
  tri-state explanation under `type --paste` above; the `method` reflects how the input's
  `Type`/paste call proceeded; the Send click/keyboard submission's own internal mechanism is not
  reported — a known simplification, see below).
- Failure: `invalid-argument` (with `"step": "validate-arguments"`) for a missing/empty `--text`;
  an embedded newline in `--text` when the resolved input has no usable `ValuePattern` and
  `--paste` is not given (same conditional rejection as `type`, duplicated here since this verb
  calls `UiaHelper.Type` directly rather than going through `Verbs.Type` — see that section above
  for the full rationale); a missing, empty, or
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
- Failure: `verify-unstable` (with `"step": "type-verify"`, plus `expected`/`actual` fields) for
  `method == "clipboard-paste"` only — same asynchronous post-paste mutation guard as `type --paste
  --verify` (see that section's Phase 14 known-limitation entry above). This verb's own send
  action (Send-button click or `--submitKeys`) happens immediately after this check, so blocking
  here on an unstable read-back specifically prevents sending an incomplete/mutated message —
  this was the original real-world trigger for adding this check.
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

### `find-first --hwnd <h> --strategy <S> --value <V> [--scopeStrategy <S> --scopeValue <V>]` (Phase 13, 2026-09-15)
Generic, shallow-cost single-match lookup: a single `FindFirst` against the resolved window (or a
narrower scope, if `--scopeStrategy`/`--scopeValue` are both supplied) — no fixed-depth tree
walk/serialization the way `inspect --maxDepth N` does. This is a general-purpose UIA primitive
with no assumption about any particular application's UI shape; callers compose whatever
app-specific detection logic they need (e.g. "is a confirmation prompt pending") out of one or
more calls to `find-first`/`find-all`, keeping app-specific patterns (element names, AutomationIds,
control types) in caller-side scripts/config rather than baked into this exe.
- `--strategy`/`--value`: same `Selector` semantics as `click`/`type`/etc. (`Name`, `AutomationId`
  today; `NameRegex`/`ControlTypeIndex`/`Coordinates` are accepted by the enum but not yet
  implemented — see "Common types" above and `docs/KNOWN_OPEN_FINDINGS.md`).
- `--scopeStrategy`/`--scopeValue` (optional, must both be supplied together or neither): resolves
  a descendant of the window first and searches under it instead of the whole window — useful to
  narrow a search to a specific panel, both for speed and to avoid ambiguous matches elsewhere in
  the window (e.g. a control that appears in both a chat panel and a code editor pane).
- Success (found): `{ "success": true, "found": true, "element": { "name", "automationId",
  "controlType", "className" } }`.
- Success (not found): `{ "success": true, "found": false }` — not an error, matching
  `wait-for-element`'s non-error "not found" convention.
- Failure: same window-resolution error codes as `inspect` (`element-not-found`/
  `ambiguous-window`/`stale-context`/etc.), `window-not-responding` (checked up front, matching
  `click`/`type`/`send-keys`/`submit-chat-message`/`find-all`), and `invalid-argument` if
  `--scopeStrategy`/`--scopeValue` are only partially supplied.

### `find-all --hwnd <h> --strategy <S> --value <V> [--scopeStrategy <S> --scopeValue <V>] [--excludeValue <V>]` (Phase 13, 2026-09-15)
Same scoping/strategy support as `find-first`, but uses `FindAll` to return every matching
descendant instead of stopping at the first.
- `--excludeValue` (optional): skips any matched element whose `Name` equals this value — a
  generic convenience for the common case of filtering out one known placeholder/fallback option
  (e.g. a catch-all "Other" choice in a list of real options). This is a plain string filter, not
  tied to any specific application's semantics.
- Success: `{ "success": true, "count": N, "elements": [ { "name", "automationId", "controlType",
  "className" }, ... ] }`.
- Failure: same error codes as `find-first`.

**Design note — supersedes the former `has-pending-prompt` verb (Phase 11).** That verb hardcoded
Copilot-Chat-specific detection patterns directly into the CLI (`Name == "Waiting..."` as the
pending-prompt anchor, `AutomationId == "RadioFieldLabel"` for the question text, and
`ControlType.RadioButton` descendants excluding the literal `"Other"` label for options) — a
design concern raised after Phase 11 shipped: the tool should stay a generic UIA interface, with
app-specific patterns living in caller-side scripts/config, not baked into `agentdebug-ui.exe`
itself. `find-first`/`find-all` replace it with fully generic primitives; a caller reproduces the
exact same check by composing them, e.g.:
```
agentdebug-ui find-first --hwnd 0xCA18B2 --strategy Name --value "Waiting..."
# if found.found == true:
agentdebug-ui find-all --hwnd 0xCA18B2 --strategy AutomationId --value RadioFieldLabel
```
Enumerating the radio-button *options* themselves (as opposed to the label) needs a
control-type-based match, which `find-all` does not yet support as a `Selector` strategy (`Name`/
`AutomationId` only today — see "Common types" above); until `ControlTypeIndex` or an equivalent
strategy is implemented, a caller can fall back to `inspect` scoped narrowly (e.g. via
`--scopeStrategy`/`--scopeValue` first, then a shallow `inspect` on just that scope) for that
specific sub-lookup. This mirrors what
`tools/Watch-CopilotChat.ps1` already does today — it never called `has-pending-prompt`; it
performs its own client-side filtering (Name/AutomationId/ControlType checks) over a full
`inspect` dump, i.e. the app-specific logic already lived in the caller script, not the CLI. No
existing caller depended on `has-pending-prompt`'s shape, so removing it is not a breaking change
in practice.

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

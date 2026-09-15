# Implementation Plan — Phased Delivery

This plan builds AgentDebugToolkit incrementally: each phase produces something runnable and
independently verifiable before the next phase begins. Do not start a phase until the previous
phase's exit criteria are met. See `ARCHITECTURE.md` for component responsibilities,
`CLI_CONTRACT.md` for exact command/JSON specs, and `NAVIGATION_MAP_SCHEMA.md` for the map file
format (owned by the agent, not the CLI).

## Guiding principles for phasing

- Start with the smallest possible slice that proves the core mechanism (Name-match +
  coordinate-based synthetic input) end-to-end, before adding convenience features.
- Each phase should be usable standalone by an agent issuing raw CLI commands, even before later
  phases exist.
- Defer anything IDE-specific (Visual Studio debugger bridge) and anything not yet validated
  (console automation, C1FlexGrid-specific handling) until the core is solid.

---

## Phase 0 — Repo scaffolding (no automation logic yet)

**Goal:** project structure exists and builds.

- Create solution with projects:
  - `src/AgentDebugToolkit.Core` (class library) — shared models: `ElementSelector`,
	`ActionResult`, `WindowInfo`, JSON serialization contracts. No UIA references yet.
  - `src/AgentDebugToolkit.UiAutomation.Cli` (console app, .NET 8) — entry point, command-line
	parsing (e.g. `System.CommandLine`), references Core.
- Add `docs/CODE_SUMMARY.md` stub (per project-memory convention) describing this structure.
- **Exit criteria:** solution builds; `agentdebug --help` (or equivalent) prints available verbs
  with no real implementation behind them yet (stubs returning "not implemented").

## Phase 1 — Core UI Automation loop (the make-or-break slice)

**Goal:** prove the validated mechanism (attach, enumerate, click, type, read text) works as a
real CLI, not just a throwaway script.

Implement only these verbs (see `CLI_CONTRACT.md` for exact JSON shapes):
- `attach --process <name>`
- `list-windows`
- `inspect --hwnd <h>` (UIA tree dump + screenshot save; no map awareness)
- `click --hwnd <h> --strategy Name --value <text>`
- `type --hwnd <h> --strategy Name --value <text> --text <input>`
- `get-text --hwnd <h> --strategy Name --value <text>`

Implementation notes:
- Click/type must implement the **pattern-first, coordinate-fallback** approach: try
  `InvokePattern`/`ValuePattern` if supported, otherwise resolve `BoundingRectangle` and issue
  synthetic input (`SendInput`). This directly reflects `VALIDATION_FINDINGS.md`.
- `Name` strategy only in this phase (exact match). `AutomationId`, `NameRegex`,
  `ControlTypeIndex`, `Coordinates` strategies come in Phase 3.
- No session-context persistence yet — every command takes `--pid` explicitly (simplifies Phase 1
  scope; add the context file in Phase 2).
- No map file involvement at all in this phase — the agent (Claude) supplies raw selectors
  directly.

**Exit criteria (manual validation against the real FDM app, mirroring the earlier PowerShell
probe):**
1. `attach --process Fdm` returns a pid and window list.
2. `inspect` on the main window returns a JSON tree + screenshot file that the agent can view.
3. `click --strategy Name --value "OK"` on the login dialog succeeds and the app advances.
4. Two more navigation clicks (e.g., into a device-type menu, then a workflow menu) succeed using
   only this CLI — no PowerShell fallback.
5. `type` successfully sets text into a native `EDIT` control (e.g., the IP address field on
   Device Control Panel) and `get-text` reads it back correctly.

Do not proceed to Phase 2 until all 5 are demonstrated.

## Phase 2 — Waiting, session context, window diffing

**Goal:** make the CLI usable for real multi-step workflows without the agent guessing timing.

- Add session-context file (`attach` persists `{pid, processName}`; subsequent commands omit
  `--pid` by default, `--pid` remains an explicit override).
- Add `wait-for-window-change --timeoutMs n --settleMs 300` (before/after window snapshot diff).
- Add `wait-for-element --strategy ... --value ... --timeoutMs n [--requireEnabled]`.
- Add `wait-for-process-responding --timeoutMs n` (hang detection).
- Add `delay --ms n` (explicit escape hatch, documented as last-resort).

**Exit criteria:**
- Demonstrate a scripted sequence: `click` (menu item) -> `wait-for-window-change` -> confirms a
  new screen rendered, reporting the diff (titles/hwnds before/after) — without any fixed
  `Sleep`/`delay` in the sequence.
- Demonstrate `wait-for-process-responding` correctly reports "responding" during normal use (a
  true hang scenario is not required to test, just confirm the plumbing/API call succeeds).

## Phase 3 — Selector robustness & additional strategies

**Goal:** cover the harder element-identification cases found in `VALIDATION_FINDINGS.md` and the
earlier scenario analysis (dynamic lists, icon-only controls, duplicate names).

- Add `NameRegex` strategy.
- Add `ControlTypeIndex` strategy (nth control of a given `ControlType` under a scope element).
- Add `Coordinates` strategy, **client-area-relative** (not absolute screen).
- Add explicit **scoping**: all element searches must accept a `--scopeHwnd` (or default to the
  attached window) rather than searching the whole desktop — required for correctness once
  multiple windows/dialogs can be open (see Phase 4).
- Spot-check one C1FlexGrid-based screen (e.g., `RegisterValuesPage`) using `inspect`; document
  findings in `VALIDATION_FINDINGS.md`. If UIA exposes nothing useful for grid cells, confirm the
  `Coordinates` + screenshot fallback is sufficient for that screen type; do not build
  grid-specific selector logic speculatively — only if the spot-check shows it's needed.

**Exit criteria:**
- All 4 selector strategies demonstrated against real elements in the FDM app.
- C1FlexGrid spot-check documented with a concrete recommendation (works via existing strategies,
  or needs a follow-up phase).

## Phase 4 — Multi-window / dialog awareness

**Goal:** support modal dialogs, secondary windows, and system dialogs reliably.

- Extend `list-windows` to report `ownerHwnd`, `isModal` (owned + popup-style check),
  `isForeground`, per window.
- Ensure all action/wait commands correctly scope to a specific hwnd rather than assuming a single
  top-level window per process.
- Verify behavior against at least one real modal dialog in the FDM app and one owner-chain case
  (e.g., a MessageBox or the earlier "Server" login-related dialog observed during validation).

**Exit criteria:**
- Demonstrate detecting a new modal dialog appearing after an action (via
  `wait-for-window-change`), acting on an element inside it (scoped to its own hwnd), and
  confirming it closes/returns focus correctly afterward.

## Phase 5 — Agent-side navigation map (no CLI changes required)

**Goal:** Claude (the agent) starts building and using the screen-graph map described in
`NAVIGATION_MAP_SCHEMA.md`, using only the CLI verbs from Phases 1–4. The CLI itself remains map-
agnostic per the earlier design decision.

- Agent creates `docs/app-navigation-map/index.json` + per-screen files as it explores the FDM
  app, using `inspect`/`list-windows` output to determine `matchRule`s and elements.
- Agent manually exercises: attach -> whereami-style reasoning (comparing `list-windows`/`inspect`
  output to the map) -> click via a selector pulled from a screen's map file -> wait -> confirm
  landed on expected `targetScreenId`.
- No new CLI code required for this phase — it validates that the CLI's Phase 1–4 output is
  sufficient for map-based reasoning without needing CLI-side map support.

**Exit criteria:**
- At least 5 real screens mapped and a 3+ step navigation chain successfully replayed by the agent
  purely from reading its own map files plus CLI calls.

## Phase 6 — Visual Studio debugger bridge (separate component)

**Goal:** add `AgentDebugToolkit.Debugger.VisualStudio`, independent of the UI automation CLI.

- New console app/project using EnvDTE to connect to a running `devenv.exe` instance.
- Verbs: `debugger-status` (running/break/design mode), `get-callstack`, `get-locals`,
  `get-exception-info` (when break mode is due to an exception), `continue`, `step-over`,
  `step-into`, `step-out`, `start-debugging`, `stop-debugging`.
- JSON output contract consistent in style with the UI automation CLI (see `CLI_CONTRACT.md`).

**Implementation status:** all 10 verbs implemented (`src/AgentDebugToolkit.Debugger.VisualStudio`,
assembly name `agentdebug-vs`); project added to `AgentDebugToolkit.slnx`. Connects via the
Running Object Table (optionally filtered by `--solution <name>`); JSON contract documented in
`CLI_CONTRACT.md`'s "Phase 6" section. All EnvDTE/COM call sites go through a shared `ComRetry`
helper that retries up to 3 times (100ms/200ms/300ms backoff) on `RPC_E_SERVERCALL_RETRYLATER`/
`RPC_E_CALL_REJECTED` before surfacing a dedicated `com-busy-retry-exhausted` error, added after
`start-debugging` validation hit a transient "VS busy" COM failure. Builds successfully and has
been validated live against a real breakpoint hit and real start/stop debugging cycles.

**Exit criteria (validated live against the FDM solution, 2026-09-14):**
- [x] With a breakpoint hit in the FDM solution in Visual Studio, `debugger-status` correctly
  reports break mode (and, if hit via a real breakpoint, the active document/line). Verified:
  hit a breakpoint in `COSEMConnectionPage.cs`, `debugger-status` reported
  `mode: "break"`, correct `activeDocument` path, and `activeLine: 176`.
- [x] `get-callstack` returns real frame data for that break. Verified: returned the full,
  accurate 21-frame call stack from `SaveWorkflowData` through `Main`.
- [x] `get-locals` returns real local variable data for the current frame. Verified: returned
  `this` (the `COSEMConnectionPage` instance) with correct type.
- [x] `continue` resumes execution and `debugger-status` subsequently reports `run`/`design`.
  Verified: `continue` succeeded, subsequent `debugger-status` reported `mode: "run"`.
- [x] `continue` with no active debug session returns a `no-active-session` error instead of
  starting a new debug session (F5 semantics) — verified no new process was launched.
- [x] `start-debugging` from design mode successfully launches the FDM app and `debugger-status`
  subsequently reports `mode: "run"`. Verified: `start-debugging` triggered a build + launch
  (VS was busy briefly, first attempt hit `RPC_E_SERVERCALL_RETRYLATER`; after adding the
  `ComRetry` wrapper, re-validated with several consecutive calls with no manual retry needed),
  `debugger-status` reported `mode: "run"`.
- [x] `start-debugging` while already running/breaking returns `already-debugging` without
  restarting the app. Verified: called again while in `run` mode, got `already-debugging`.
- [x] `stop-debugging` while running successfully terminates the debug session and
  `debugger-status` subsequently reports `mode: "design"`. Verified: `stop-debugging` returned
  `mode: "design"`, confirmed by a follow-up `debugger-status` call.
- [x] `stop-debugging` while already in design mode returns `no-active-session`. Verified: called
  before starting a session, got `no-active-session`.

## Phase 7 — Console application automation (bare minimum)

**Goal:** add `AgentDebugToolkit.ConsoleAutomation.Cli`, independent of the UI automation CLI
and the Visual Studio debugger bridge. Console apps have no UI Automation tree, so this uses a
completely different mechanism: launch the target under a pseudoconsole (ConPTY) and interact
via its text screen buffer, not UIA elements.

**Validation target:** `CAMFWDownloadConsole` (in a separate solution/repo,
`C:\ItronProjects\FDM\Repos\Solutions\CAMFWDownloadConsole`) — a real console app already
present in this environment, launched directly by exe path (no project reference needed).

- `src/AgentDebugToolkit.ConsoleAutomation.Cli` is implemented as an independent
  `net8.0-windows` console project (`agentdebug-console`) using direct ConPTY P/Invokes. It has
  no dependency on Core, UI Automation, or the Visual Studio debugger bridge.
- Implemented verbs: `launch`, `read-screen`, `send-text`, `send-keys`, `wait-for-text`,
  `is-running`, and `stop`. Their JSON contracts are documented in `CLI_CONTRACT.md`.
- `launch` creates a detached broker that owns the ConPTY session, target, output reader,
  terminal buffer, and named-pipe server. Short-lived public commands communicate through the
  persisted session's pipe name; broker PID/start-time validation rejects stale sessions.
- The broker uses line-delimited, one-request-per-connection named-pipe transport, bounded
  request reads, isolated malformed/disconnected clients, and a bounded single-writer input
  queue. Target liveness uses `WaitForSingleObject`, preserving a real exit code 259.
- `--args` remains a single target-argument value even when it begins with `--`; `read-screen`
  returns the ConPTY terminal buffer rather than a native console window.

**Implementation validation (non-CAM target, 2026-09-15):**
- Immediate `cmd.exe /c` and interactive `cmd.exe /k` ConPTY output capture succeeded.
- Exit code 259 was reported as stopped with exit code 259.
- Newline-less, malformed, and disconnected pipe clients were isolated; bounded requests released
  stalled clients and later valid requests succeeded.
- Public interactive smoke testing captured `PUBLIC_SCREEN_TOKEN`; `wait-for-text` returned its
  timeout without waiting for the entire polling interval.
- Shutdown terminated the target, drained final output, and exited the broker.
- The full solution built successfully with `DOTNET_ROOT` cleared.

**Exit criteria:**
- [x] `launch --exe <path to CAMFWDownloadConsole.exe>` starts the process and `read-screen`
  returns real captured output. Verified: captured the device endpoint, meter-definition loading,
  connection attempt, connection error, and `Press any key to exit` prompt.
- [x] `send-text`/`send-keys` inject terminal input. Verified: `send-text --text x` was echoed
  into the screen buffer and exited the target; `send-keys --keys ENTER` also exited the target.
- [x] `wait-for-text` correctly detects an expected string without requiring a fixed sleep.
  Verified: detected `Press any key to exit` from `CAMFWDownloadConsole`.
- [x] `is-running` correctly reports process liveness before and after the process exits.
  Verified: reported `running: true` before input and `running: false, exitCode: 0` afterward.

The JSON contract is documented in `docs/CLI_CONTRACT.md`.

## Phase 8 — Read-only IDE chat observation

**Goal:** allow the agent to inspect visible text and capture screenshots from an existing
application or IDE window, including a Visual Studio Copilot Chat panel, without interacting
with or changing the target UI.

- Harden `inspect --hwnd <h>` so non-finite UI Automation bounding-rectangle values cannot
  prevent a valid JSON tree response. Report unavailable bounds accurately rather than
  serializing invalid numeric values or inventing coordinates.
- Add `read-visible-text --hwnd <h>` to `AgentDebugToolkit.UiAutomation.Cli`. Traverse bounded
  UIA descendants and return de-duplicated, ordered visible text extracted opportunistically
  from `TextPattern`, `ValuePattern`, and element `Name`.
- Add `screenshot --hwnd <h>` as an explicit read-only fallback for custom controls or WebView
  content that does not expose meaningful transcript text through UIA.
- Neither verb may activate/focus the target window, invoke a UIA pattern, click, type, or alter
  the target process state.
- Bound UIA traversal, output size, and individual text values so complex IDE accessibility trees
  cannot hang the CLI or create unbounded JSON output.
- Document the JSON contracts and limitations in `docs/CLI_CONTRACT.md`. `read-visible-text` is
  best-effort; screenshot remains the fallback when UIA does not expose the actual chat content.

**Implementation notes (2026-09-15):**
- `UiaHelper.ToElementInfo`/`ToWindowInfo` now convert `BoundingRectangle` via a `SafeRect` helper
  that returns `null` instead of a non-finite (`NaN`/`Infinity`) `Rect`, since `System.Text.Json`
  cannot serialize non-finite doubles; `boundingRect` is omitted (not emitted as JSON `null`, per
  `JsonOutput`'s existing null-field-omission default) when this occurs.
- `read-visible-text` reuses `inspect`'s window-resolution helpers plus the same
  `NativeMethods.IsResponding` pre-check `click`/`type` use, so it fails fast with
  `window-not-responding` rather than risking a block on a hung target's UIA calls.
  `UiaHelper.CollectVisibleText` bounds traversal (200 children/element, 5000 total visited nodes,
  2000 characters/value, default depth 8) and reports a `truncated` flag when any cap is hit.
- `screenshot` captures via `PrintWindow` with `PW_RENDERFULLCONTENT` first (works correctly for
  DirectComposition/WPF-rendered windows like Visual Studio's own UI, and captures the target
  window's own content regardless of z-order/obscurity), falling back to a `CopyFromScreen`
  capture of the window's screen rect only if `PrintWindow` reports failure.

**Exit criteria:**
- [x] `inspect --hwnd <Visual Studio hwnd> --maxDepth 4` succeeds with valid JSON even when some
  descendants have invalid or unavailable bounds. Verified via code review and build; `SafeRect`
  guards every `BoundingRect` assignment in `ToWindowInfo`/`ToElementInfo`.
- [x] `read-visible-text --hwnd <Visual Studio hwnd>` succeeds without changing focus or UI state
  and reports whether actual Copilot Chat transcript text is available through UIA. Verified live
  (2026-09-15) against a real running Visual Studio Insiders instance (hwnd rediscovered via
  `list-windows --pid <devenv pid>`): returned the actual Copilot Chat transcript text (prompt
  content, chat responses, panel chrome), with `truncated: true` given the window's large
  accessibility tree. Confirms Copilot Chat text is UIA-exposed in this environment.
- [x] `screenshot --hwnd <Visual Studio hwnd>` returns an existing PNG path without changing focus
  or UI state. Verified live (2026-09-15): first validated the `PrintWindow` fallback risk by
  capturing while the Visual Studio window was fully obscured on-screen by another application
  window — the initial `CopyFromScreen`-only implementation captured the wrong (obscuring)
  window's pixels, which was then fixed by adding `PrintWindow`/`PW_RENDERFULLCONTENT` as the
  primary capture method; re-validated afterward and confirmed the PNG correctly showed the
  target Visual Studio window's own content (menu bar, editor, Chat panel with correct text) even
  while obscured.
- [x] Existing UI Automation CLI commands retain their behavior. Verified via full solution build
  and code review; `attach`/`list-windows`/`inspect`/`click`/`type`/`get-text`/`wait-for-element`
  are unchanged apart from the shared `SafeRect` bounding-rect fix.

## Phase 9+ (future, not yet scoped in detail)

- Additional IDE bridges (VS Code via DAP, etc.) as independent components.
- OCR/template-image fallback for fully non-UIA-exposed custom controls, if Phase 3's C1FlexGrid
  spot-check or other findings show a real need.

## Phase 20 — Breakpoint verbs & wait-for-break (proposed, not yet implemented)

**Motivation:** driving a "UI Debug Map" skill (click a UI element in a target app, correlate the
resulting code path back to source) requires programmatically setting/clearing breakpoints and
waiting for a break event, neither of which `AgentDebugToolkit.Debugger.VisualStudio` (`agentdebug-vs`)
currently exposes. Phase 6 only added *passive* reads of debugger state
(`debugger-status`/`get-callstack`/`get-locals`/`get-exception-info`) plus session control
(`continue`/`step-*`/`start-debugging`/`stop-debugging`) — there is no verb to place a breakpoint,
and no event-driven wait primitive for a break (only manual `debugger-status` polling).

**Proposed scope:**
- `set-breakpoint --file <path> --line <n> [--solution <name>]` — add a breakpoint via
  `DTE.Debugger.Breakpoints.Add(File:=..., Line:=...)`. Returns an identifier (e.g. file+line, or
  an internal index) the caller can use to remove it later.
- `remove-breakpoint --file <path> --line <n> [--solution <name>]` and/or
  `remove-breakpoint --all [--solution <name>]` — clear a specific breakpoint or all of them.
- `list-breakpoints [--solution <name>]` — enumerate currently set breakpoints (file, line,
  enabled state) for the attached VS instance.
- `wait-for-break --timeoutMs <n> [--pollMs <n>] [--solution <name>]` — analogous to the UI
  automation CLI's `wait-for-element`; polls `debugger-status` internally so the skill/caller
  doesn't need to hand-roll a polling loop, and returns the same `debugger-status` payload
  (mode/activeDocument/activeLine) once break mode is entered, or a `timeout` error.

**JSON contract:** consistent in style with existing Phase 6 verbs and `CLI_CONTRACT.md` overall
(error envelope, `--solution` filtering via the Running Object Table, `ComRetry` wrapping on all
new EnvDTE/COM call sites per the Phase 6 precedent).

**Not yet scoped in detail:** conditional breakpoints, hit-count breakpoints, tracepoints —
out of scope unless a concrete future need arises.

**Planned execution:** to be implemented via the Agent Orchestrator skill (see FDM repo
`.copilot/skills/agent-orchestrator/`) once that skill exists, following the same
Pre-Build Decomposition → implement → Regression Audit → review → commit discipline used for
Phases 15-19.

## Phase 9 — Reliable interaction primitives (proposed scope, not yet implemented)

**Status:** proposed for review only (2026-09-15) — items below are documented as candidate scope
per the Pre-Build Decomposition Protocol; none are implemented yet pending explicit approval.

- **`send-keys` verb — IMPLEMENTED (2026-09-15).** A companion to the existing `type` verb (which
  sends literal text only, via `ValuePattern.SetValue` or synthetic keyboard input). `type` cannot
  express control key sequences (e.g. `Ctrl+A`, `Delete`, `Enter`) because `NativeMethods.SendText`
  escapes `SendKeys` special characters before sending, specifically to prevent literal input text
  from being misinterpreted as `SendKeys` syntax. `send-keys --hwnd <h> --strategy <s> --value <v>
  --keys <SendKeys syntax>` instead accepts and passes through unescaped `SendKeys.SendWait`
  syntax, element-scoped consistent with `click`/`type`. Returns `{ "success": true, "sent": true
  }` on success; `invalid-argument` for missing/empty `--keys` or malformed `SendKeys` syntax;
  same window/element-resolution codes as `click`/`type` otherwise. See `docs/CLI_CONTRACT.md` for
  the full contract. Regression Audit found one blocking issue (a Recurrence Escalation: the 2nd
  occurrence of the same underlying pattern as `activate`'s earlier fix — malformed caller-supplied
  syntax, here an unbalanced `{` or unrecognized key name passed to `SendKeys.SendWait`, was
  uncaught and would have surfaced as `unhandled-exception` instead of `invalid-argument`) — fixed
  by wrapping the call in a try/catch translating `ArgumentException`/`InvalidOperationException`/
  `FormatException` to `invalid-argument`. Live-validated against the same VS Insiders window's
  Copilot Chat input: an initial attempt using `--strategy AutomationId --value WpfTextView`
  resolved ambiguously to the code editor pane instead of the chat input (that `AutomationId` is
  not unique to chat), so `--strategy Name --value "Ask Copilot"` (the placeholder Name shown when
  the input is empty) was used instead — confirmed unique and correct. Sent `--keys "test"`,
  confirmed via `read-visible-text` that "test" landed in the chat input, then cleared it via
  `--keys "^a{DEL}"`, confirmed cleared via screenshot. Also confirmed malformed syntax
  (`--keys "{"`) returns a clean `invalid-argument`.
- **`activate` verb — IMPLEMENTED (2026-09-15).** Wraps `NativeMethods.SetForegroundWindow`
  (previously declared but unused) so a caller can explicitly bring a window to the foreground
  before `click`/`type`. `activate --hwnd <h>` returns `{ "success": true, "activated": true }`
  on success; `invalid-argument` for a missing/malformed `--hwnd`; `stale-context` if
  `SetForegroundWindow` returns `false` (window closed, or Windows denied the switch via
  focus-stealing prevention — documented as non-fatal, caller may retry/fall back to manual
  activation). Unlike every Phase 8 verb, this is intentionally interactive/non-read-only.
  See `docs/CLI_CONTRACT.md` for the full contract. Regression Audit found one blocking issue
  (the initial `catch (FormatException or OverflowException)` filter missed
  `ArgumentOutOfRangeException`, thrown by `ParseHwnd`'s underlying `Convert.ToInt64` for an
  empty/`"0x"` `--hwnd` value, which would have surfaced as `unhandled-exception` instead of
  `invalid-argument`) — fixed by broadening the filter to also catch `ArgumentException`.
  Live-validated against the same VS Insiders window (hwnd `0xCA18B2`): backgrounded it with
  Notepad, confirmed `isForeground: false` via `list-windows`, then called `activate` from a
  shell that was itself not the foreground process — `SetForegroundWindow` returned `false` and
  the verb correctly reported `stale-context`, demonstrating the documented focus-stealing
  prevention path live rather than a plain success case. Also validated all `invalid-argument`
  paths (missing/empty/`"0x"`/non-hex `--hwnd`) return cleanly with no unhandled exception.
- **`type --verify` flag — IMPLEMENTED (2026-09-15).** An optional flag on the existing `type`
  verb that re-reads the element (via the same mechanism as `get-text`) after typing and fails
  with `verify-mismatch` (includes `expected`/`actual` fields) if the read-back value doesn't
  match the input text — surfacing silent typing failures that `type`'s plain success response
  can't detect on its own. See `docs/CLI_CONTRACT.md` for the full contract. Regression Audit
  found two blocking issues: (1) the initial implementation used presence-only detection
  (`opts.ContainsKey("verify")`), diverging from the existing `--screenshot` boolean-flag
  convention (no way to pass `--verify false` to disable) — fixed to match that convention; (2)
  critical: verification would produce a false-positive `verify-mismatch` for any element without
  `ValuePattern` support, because `Type`'s synthetic-keyboard fallback has no corresponding
  `ValuePattern`-based read-back, so `GetText` falls back to the element's static accessibility
  `Name` instead of its typed content — comparing that against the typed text would almost always
  mismatch even on a fully successful type. Fixed by scoping verification to only run when
  `method == "pattern"`, silently skipping it (not erroring) for `"synthetic-keyboard"` results.
  Live-validated against the same VS Insiders Copilot Chat input (a synthetic-keyboard-only
  control): `type --verify` returned `{ "method": "synthetic-keyboard", "success": true }` with
  no spurious `verify-mismatch`, confirming the skip logic. The `verify-mismatch` failure path
  itself (for `ValuePattern`-backed controls) was validated by code review/build only, not live,
  per user direction — no safe `ValuePattern`-backed control was available in this session to
  deliberately mistype into.
- **`submit-chat-message` composite verb — IMPLEMENTED (2026-09-15).** A composite verb tailored
  to the Copilot Chat input pattern: given `--hwnd`, an input `AutomationId`, a Send-button
  `AutomationId`, and `--text`, it clicks the input, types the text, verifies it landed via
  read-back (when possible), then clicks Send — as a single call instead of a caller scripting the
  equivalent three-call sequence. See `docs/CLI_CONTRACT.md` for the full contract, error `step`
  values, and known limitations. Regression Audit found one medium finding (reusing
  `UiaHelper.Click` for the input's pre-type focus click risked an unintended
  invoke/toggle side effect if the input happened to expose those patterns — fixed by using a
  plain `NativeMethods.Click` synthetic click on the input's bounding-rect center instead, since
  `UiaHelper.Click`'s invoke/toggle-then-synthetic-click ordering is a real behavioral difference
  from a "just focus this" click) and one minor finding (the three upfront `invalid-argument`
  argument-validation checks omitted the `step` field present on every other failure path — fixed
  by adding `step = "validate-arguments"`). Non-blocking notes from the audit: partial-typed state
  on `verify-mismatch` failure (inherent to a non-transactional composite verb, documented as a
  known limitation), no ambiguity detection on `AutomationId` lookups (pre-existing
  `ResolveSelector` limitation, out of scope), and the two internal `Click` calls' own methods
  being discarded from the response (acceptable — the response reports `Type`'s method only, a
  documented simplification). **Live validation could not be completed end-to-end**: a full tree
  inspection of the real, running VS Insiders window (`inspect --hwnd 0xCA18B2 --maxDepth 15`)
  found no Send-button-like `AutomationId` anywhere, and the only `AutomationId` matching
  `WpfTextView` resolves to the code editor pane, not the chat input (consistent with `send-keys`'s
  earlier finding that the chat input needed a `Name`-based selector instead) — the real Copilot
  Chat panel's input and Send button are not resolvable via `AutomationId` at all in this session,
  so this verb's core design assumption doesn't hold for this specific chat UI. Validation is
  therefore code-review/build-only; the individual primitives it composes (window/element
  resolution, click, type, verify, click) are each independently live-validated by `click`,
  `type`, `type --verify`, and `send-keys` above.
- **`type` embedded-newline rejection — IMPLEMENTED (2026-09-15), a real behavior change.** Per
  explicit user decision, `type` (and `submit-chat-message`, which calls `UiaHelper.Type` directly)
  now reject `--text` containing an embedded `\n`/`\r` with `invalid-argument`, checked before
  window/element resolution — previously a raw newline reached the underlying `SendKeys.SendWait`
  call on the synthetic-keyboard fallback path uninterpreted-as-literal, observed live to trigger
  unintended UI navigation (unexpectedly focusing a different control) rather than being typed as
  literal text. Regression Audit found the rejection is unconditional across both of `type`'s
  paths, including `ValuePattern.SetValue` (where the observed bug cannot occur, since no
  `SendKeys` call is involved) — meaning a multi-line `ValuePattern`-backed control cannot
  currently receive newline text via `type` at all. Per explicit user decision, this was kept as a
  deliberate simplicity/uniformity tradeoff (one consistent rule across both paths) rather than
  made path-aware, and is documented as such in `docs/CLI_CONTRACT.md`. Live-validated: `type
  --text "line1\nline2"` against the real Copilot Chat input returned a clean `invalid-argument`
  with no unhandled exception and no attempt to send the text.
- **`activate`/`SetForegroundWindow` caller-focus-state limitation — already documented.** Verified
  this Phase 9 item is satisfied by the "Known limitation — foreground denial depends on the
  caller's own focus state" note already added to `docs/CLI_CONTRACT.md`'s `activate` section
  during the `send-keys`/Part A documentation pass — no separate change was needed.

  `PW_RENDERFULLCONTENT` capture (Phase 8) can return an incomplete or incorrectly scaled frame
  for a non-foreground/non-visible window, depending on how the target renders when not actively
  composited — this should be recorded as a known constraint in `docs/CLI_CONTRACT.md` (and
  potentially `docs/KNOWN_OPEN_FINDINGS.md`, pending explicit confirmation per that file's rules)
  rather than treated as a bug to fix outright; the proposed `activate` verb above is one way an
  agent could work around it by bringing the window to foreground first.

**Not yet decided at proposal stage (to be resolved during breakdown/implementation):**
- Whether `send-keys` is element-scoped (like `click`/`type`) or window-scoped only.
- The exact error code name for `type --verify`'s mismatch case.
- Whether the `PrintWindow` non-foreground limitation is best captured only as contract
  documentation, or also as a `docs/KNOWN_OPEN_FINDINGS.md` entry (requires explicit user
  confirmation per that file's rules before being added).

---

## Out of scope for now (explicitly deferred, do not build speculatively)

- Any CLI-side awareness of the navigation map (ownership stays with the agent, per design
  decision).
- Grid-cell-specific selector strategies, unless Phase 3's spot-check proves they're needed.
- Any non-Visual-Studio debugger integration.

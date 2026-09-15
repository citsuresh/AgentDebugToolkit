# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

Phase 11 (`has-pending-prompt --hwnd <h>` verb) is implemented, committed (`41bf2de`), and
pushed to `origin/main`. A lightweight alternative to a costly `inspect --maxDepth 20` poll for
detecting whether the Copilot Chat agent is blocked on a confirmation/prompt card: anchor-based
detection via `ResolveSelector` on `Name=="Waiting..."`; when found, scoped `FindAll` lookups
extract the question (`AutomationId=RadioFieldLabel`) and options (`ControlType.RadioButton`,
excluding `"Other"`). Output: `{ success, pending: false }` (cheap path) or `{ success,
pending: true, question, options }`. Benchmarked ~4x faster than `inspect --maxDepth 20`
(~1.2-1.9s vs ~5.8-12.5s). An independent Regression Auditor found one Medium finding (the two
`FindAll` calls were unguarded by try/catch, unlike every other `FindAll` site in the codebase)
— fixed and re-verified by a follow-up audit pass before commit.

This session also independently live-validated `send-keys` (previously only informally
exercised during an earlier audit) against a disposable, isolated Notepad instance (not this VS
window) — sent literal text, verified via `get-text`, then sent `^a{DEL}` and verified the field
was cleared. Both the literal-text and key-combo paths behaved as expected; no bug found, no
code changes made. Test instance closed after validation.

Phase 9 (reliable interaction primitives) is implemented, committed (`4178912`), and pushed to
`origin/main`. New verbs: `activate --hwnd <h>` (SetForegroundWindow wrapper), `send-keys --hwnd
<h> --strategy <s> --value <v> --keys <SendKeys syntax>` (raw key-combo pass-through, companion to
`type`), `type --verify` (opt-in read-back verification, scoped to `ValuePattern`-backed controls
only to avoid false positives on synthetic-keyboard controls), and `submit-chat-message --hwnd <h>
--inputAutomationId <id> --sendAutomationId <id> --text <input>` (composite click/type/verify/Send
verb for the Copilot Chat input pattern). `type` (and `submit-chat-message`) now reject embedded
newlines in `--text` with `invalid-argument` (a real behavior change) since a raw newline via
`SendKeys` previously triggered unintended UI navigation. All items followed the Pre-Build
Decomposition + Regression Auditor discipline; each part was audited and any findings fixed before
proceeding.

Phase 10 (chat composer selector gap fix) is implemented, committed (`94c5f15`), and pushed to
`origin/main`, addressing the known Phase 9 limitation that the real Visual Studio Copilot Chat
composer/Send button expose no usable `AutomationId`. `submit-chat-message` now accepts either
`--inputAutomationId <id>` (original, preserved for compatibility) or `--inputStrategy Name
--inputValue <value>` (new — matches the composer's `Name="Ask Copilot"` while empty) for input
selection, and either `--sendAutomationId <id>` (original, preserved) or `--submitKeys <SendKeys
syntax>` (new, defaults to `{ENTER}` — sends keyboard input to the input element instead of
clicking a Send button) for submission. The two modes in each pair are mutually exclusive,
enforced with `invalid-argument`. An independent Regression Auditor subagent confirmed the
original `--inputAutomationId`/`--sendAutomationId` compatibility path is behaviorally unchanged
(same validation order, error codes, success shape) and found no other issues. `docs/CLI_CONTRACT.md`
updated accordingly. Live end-to-end validation of the new Name+submitKeys path was explicitly
deferred by user choice (code-review/build-only, consistent with how Phase 9 documented this same
gap) — see Open Tasks. **Update (2026-09-15): live-validated end-to-end** against this session's
own VS Insiders window (hwnd `0xCA18B2`) — see Open Tasks entry for the exact command/result.

## Open Tasks / Known Issues

- `submit-chat-message`'s new `--inputStrategy Name --inputValue <value>` / `--submitKeys` path
  (Phase 10) was live-validated end-to-end (2026-09-15) against this session's own VS Insiders
  window (hwnd `0xCA18B2`): `submit-chat-message --hwnd 0xCA18B2 --inputStrategy Name
  --inputValue "Ask Copilot" --text "ping-test-phase10"` resolved the composer by `Name`, typed
  the text (`method: "synthetic-keyboard"`), and submitted it via the default `--submitKeys
  {ENTER}` with no Send-button `AutomationId` needed — `{"method":"synthetic-keyboard","success":true}`.
  This closes the Phase 9 gap below.
  - Phase 11 (`has-pending-prompt --hwnd <h>` verb) is implemented, committed (`41bf2de`), and
    pushed. Anchor-based detection via a single `FindFirst`-equivalent (`ResolveSelector` with
    `Strategy=Name, Value="Waiting..."`); when found, two scoped `FindAll` lookups extract the
    question (`AutomationId=RadioFieldLabel` nodes) and options (`ControlType.RadioButton` nodes,
    excluding literal `"Other"`). Output: `{ success, pending: false }` (cheap path, no
    `FindAll` performed) or `{ success, pending: true, question, options }`. No `--maxDepth`
    option — detection is anchor-based, not depth-bounded. Live-validated: `pending: false`
    (idle) multiple times, and `pending: true` against a real freeform (non-radio) confirmation
    card (`question`/`options` legitimately empty in that case — no RadioFieldLabel/RadioButton
    nodes exist for a freeform prompt). Benchmarked ~4x faster than `inspect --maxDepth 20`
    (~1.2-1.9s vs ~5.8-12.5s via direct .exe). An independent Regression Auditor found one
    Medium finding — the two `FindAll` calls were unguarded by try/catch, inconsistent with
    every other `FindAll` site in the codebase (`UiaHelper.ListTopLevelWindows`/`ToElementInfo`/
    `CollectVisibleText`), a real risk since this verb polls an actively-changing chat panel. Fix
    applied (both wrapped in try/catch, degrading to empty question/options on a transient UIA
    exception) and re-verified by a follow-up Regression Auditor pass before commit.
  - **Remaining gap:** the radio-button `question`/`options` extraction path has NOT been
    independently live-validated against an actual radio-button-style confirmation card (no such
    card appeared during this session's live testing — only a freeform text-field card was
    available). Worth validating opportunistically if/when a radio-button card next appears.
  - `send-keys` was independently live-validated this session against a disposable, isolated
    Notepad instance (hwnd separate from this VS window): sent literal text via `send-keys`,
    verified with `get-text`, then sent `^a{DEL}` and verified the field was cleared. Both the
    literal-text and key-combo paths behaved as expected — no bug found, no code changes made.
- (Phase 9 finding, now addressed by the above) The real Copilot Chat panel in the test VS
  Insiders instance (hwnd `0xCA18B2`, pid `141556`) exposes no discoverable `AutomationId` for its
  input or Send button (confirmed via full tree inspection) — `submit-chat-message`'s original
  `AutomationId`-only design didn't match this specific chat UI. See `docs/CLI_CONTRACT.md` for
  details.
- `type`'s embedded-newline rejection is unconditional across both its `ValuePattern` and
  synthetic-keyboard paths, even though the underlying bug (SendKeys-driven UI navigation) only
  affects the synthetic-keyboard path — a deliberate simplicity/uniformity tradeoff, not a fix
  pending. Multi-line `ValuePattern`-backed controls currently cannot receive newline text via
  `type` at all.
- `screenshot`'s `PrintWindow`/`PW_RENDERFULLCONTENT` capture (Phase 8) can return an incomplete or
  incorrectly scaled frame for a non-foreground/non-visible window — proposed as a
  known-limitation doc item during Phase 9 breakdown but not yet written up.
- Validate Phase 7 exit criteria against `CAMFWDownloadConsole.exe`; current validation uses
  `cmd.exe` smoke targets only.
- Phase 2 UI Automation gaps remain: `wait-for-window-change`, `wait-for-process-responding`,
  `delay`, and `set-context` are documented but not dispatched; `--scopeHwnd` is ignored by
  `click`/`type`; and `attach --process <name>.exe` does not normalize the suffix. Explicitly out
  of scope unless directed otherwise.

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs` (Phase 9: added `Verbs.Activate`,
  `Verbs.SendKeys`, `Verbs.SubmitChatMessage`; extended `Verbs.Type` with `--verify` and newline
  rejection; new dispatch cases. Phase 10: extended `Verbs.SubmitChatMessage` with
  `--inputStrategy`/`--inputValue` and `--submitKeys` argument modes, mutually exclusive with the
  original `--inputAutomationId`/`--sendAutomationId` modes. Phase 11 [uncommitted]: added
  `Verbs.HasPendingPrompt` and its `has-pending-prompt` dispatch case)
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` (added `SendKeys(element, keys)`, Phase 9)
- `src/AgentDebugToolkit.UiAutomation.Cli/NativeMethods.cs` (added `SendKeysRaw`, Phase 9)
- `docs/CLI_CONTRACT.md`, `docs/IMPLEMENTATION_PLAN.md` (Phase 9 verb contracts, audit findings,
  live-validation notes, known limitations; `docs/CLI_CONTRACT.md` updated again for Phase 10's
  `submit-chat-message` argument modes, and for Phase 11's `has-pending-prompt` contract
  [uncommitted])


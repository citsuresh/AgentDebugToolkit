# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

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
gap) — see Open Tasks.

## Open Tasks / Known Issues

- `submit-chat-message`'s new `--inputStrategy Name --inputValue <value>` / `--submitKeys` path
  (Phase 10, added to fix the `AutomationId` gap below) has not been live-validated end-to-end —
  only code-review/build validation was done, by explicit user choice, to avoid submitting a real
  test message into the live chat session being used for this work. A caller adopting this path
  should validate it live against their own Copilot Chat window before relying on it.
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
  original `--inputAutomationId`/`--sendAutomationId` modes)
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` (added `SendKeys(element, keys)`, Phase 9)
- `src/AgentDebugToolkit.UiAutomation.Cli/NativeMethods.cs` (added `SendKeysRaw`, Phase 9)
- `docs/CLI_CONTRACT.md`, `docs/IMPLEMENTATION_PLAN.md` (Phase 9 verb contracts, audit findings,
  live-validation notes, known limitations; `docs/CLI_CONTRACT.md` updated again for Phase 10's
  `submit-chat-message` argument modes)


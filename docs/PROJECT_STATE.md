# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

Phase 14 (paste-verify stability guard against asynchronous post-paste mutation) is implemented,
committed (`a3debf6`), and pushed to `origin/main`. Real bug reported by the user: pasting a
multi-paragraph message (blank-line paragraph breaks) into the VS Copilot Chat composer via
`type --paste --verify` reported success (`method: "clipboard-paste"`, no `verify-mismatch`), but
only a truncated first-line fragment actually landed/was sent -- reproduced twice with the same
message, resolved only by collapsing to a single line. Root cause (unconfirmed but consistent with
observed symptom): some JS-driven composer controls (unlike native Win32 edit controls) treat an
embedded newline as a submit trigger, or otherwise mutate pasted content, asynchronously -- after
our one-shot synchronous verification read already looked complete. This is app-side target
behavior, not a defect in the paste/verify mechanism, and cannot be fully eliminated from the
automation side.

Fix (Pre-Build Decomposition Part A+B+C, all approved and implemented): added
`VerifyPasteStability(element, text, firstRead)`, invoked only for `method == "clipboard-paste"`
after the existing exact-match check passes. (A) Re-reads the element after an additional ~250ms
settle delay and compares to the first read; a mismatch (content changed/shrank) fails as
`verify-unstable` instead of reporting success. (B) Independently flags the first read as
suspicious if its length is under 50% of the input length even though it nominally matched --
guards a partial-prefix false positive. Wired into both `type --paste --verify` and
`submit-chat-message` (critically, this check happens *before* the Send-button click/`--submitKeys`
call in the latter, so an unstable paste blocks sending rather than being sent -- the original
real-world trigger for this fix). `docs/CLI_CONTRACT.md` updated with a Phase 14 known-limitation/
mitigation entry under both `type` and `submit-chat-message`.

Two Regression Auditor passes run (one on the initial diff, one narrow follow-up on the fixes
applied from the first): first pass found (1) the error payload reported the stale first read as
`actual` even when the second (mutated) read was the real evidence of failure, and (2) a cosmetic
brace-collapse (`}}` on one line) at both call sites that obscured block structure without being a
functional bug. Both fixed: `VerifyPasteStability`'s return tuple now includes the specific
`actual` read that triggered the failure (first read for the shrinkage case, second read for the
instability case), and both call sites use that returned value rather than their own local
`actual`; braces re-split onto separate lines. Follow-up audit confirmed both fixes correct, no
new issues, build clean (0 warnings/errors) both times.

Item 3 (radio-button-path live validation, previously open) -- superseded/dropped after user
clarification mid-session confirmed it as no longer applicable to current work; not acted on this
session, no explicit closure needed (see prior session's history for context if revisited).

Phase 2 UI Automation gaps (previous session's task, completed and pushed as `06850e6` before this
session's Phase 14 work): `wait-for-window-change`, `wait-for-process-responding`, `delay`, and
`set-context` verbs -- previously documented but never dispatched -- are now implemented and wired
into the CLI's dispatch switch. `wait-for-process-responding` was refined (per user's choice,
during Regression Audit follow-up) to prefer the process's single foreground/unambiguous window
over "any window responds" semantics, mirroring `ResolveWindowHwnd`'s existing foreground-preference
pattern, falling back to "any window responds" only when no single unambiguous window exists.
`--scopeHwnd` now actually scopes `click`/`type` (previously accepted but silently ignored) via a
fix to the `ResolveElement(hwnd, opts)` overload. `attach --process` now normalizes a trailing
`.exe` suffix (case-insensitive) before matching, while error messages still show the user's
original input. `docs/CLI_CONTRACT.md` updated throughout (implementation-status notes, per-verb
docs, "Fixed 2026-09-15" notes on `attach`/`click`, and `wait-for-process-responding`'s doc entry
rewritten to describe the foreground-preference semantics). Regression Audit run (one flagged
issue -- the any-window-responds semantics -- resolved via explicit user choice as described
above); build clean throughout.

Also this session (prior to Phase 2 work): revisited the `type`/`submit-chat-message` embedded-
newline rejection (previously unconditional) -- narrowed to apply only to the synthetic-keyboard
path, since `ValuePattern`-backed elements never touch `SendKeys` (the underlying bug's mechanism)
and can safely receive embedded newlines directly via `SetValue`. Committed/pushed as `dee237e`
along with a doc-only addition: `screenshot`'s `PrintWindow`/`PW_RENDERFULLCONTENT` known
limitation for non-foreground/non-visible windows, now documented in `CLI_CONTRACT.md` (previously
flagged during Phase 9 but never written up).

Phase 13 (generic `find-first`/`find-all` verbs, replacing `has-pending-prompt`), Phase 12
(clipboard-paste fallback), Phase 11 (removed in Phase 13), Phase 9/10, and earlier phases remain
implemented, committed, and pushed -- see git history (`ae45478`, `380079d`, `41bf2de`, `4178912`,
`94c5f15`) for details; unchanged this session.

## Open Tasks / Known Issues

- **Radio-button prompt detection path still not independently re-validated since the Phase 13
  redesign.** No safe/disposable real radio-button-style confirmation card was available to trigger
  during this session either; still worth validating opportunistically via `find-first`/`find-all`
  next time one appears (see `CLI_CONTRACT.md`'s migration note for the caller-side approach).
- `find-first`/`find-all`'s `--scopeStrategy`/`--scopeValue` narrowing was validated only for the
  argument-mismatch and default-to-window-root cases; the actual narrowed-scope-resolves-and-narrows
  path remains code-reviewed only, not independently live-tested.
  - Enumerating radio-button *options* via `find-all` still needs a control-type-based match, not
    yet a supported `Selector` strategy (`Name`/`AutomationId` only today) -- documented as a known
    gap in `CLI_CONTRACT.md`'s migration note, with `inspect` as the fallback.
- Validate Phase 7 exit criteria against `CAMFWDownloadConsole.exe` -- **resolved this project as
  not actually outstanding**: `IMPLEMENTATION_PLAN.md` already shows all 4 exit criteria checked
  off with real `CAMFWDownloadConsole.exe` output from an earlier session; no further action needed.
- See `docs/KNOWN_OPEN_FINDINGS.md` for user-curated deferred findings (clipboard-unrelated:
  `SessionContext.Save()` file-move race, `JsonOutput` null-field omission, unimplemented selector
  strategies throwing uncaught exceptions, `DteLocator` COM object leaks, `nuget.config` scope).

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs`:
  - Newline-rejection narrowed to synthetic-keyboard-only path in `Verbs.Type`/
    `Verbs.SubmitChatMessage` (committed `dee237e`).
  - Added `Verbs.WaitForWindowChange`, `Verbs.WaitForProcessResponding` (with foreground-preference
    refinement), `Verbs.Delay`, `Verbs.SetContext` plus their dispatch cases; `--scopeHwnd` support
    in `ResolveElement(hwnd, opts)`; `.exe`-suffix normalization in `Attach` (committed `06850e6`).
  - Added `VerifyPasteStability` helper (with `PasteStabilitySettleMs`/
    `PasteSuspiciousShrinkageThreshold` constants) and wired it into `Verbs.Type` and
    `Verbs.SubmitChatMessage`'s verify blocks; return tuple includes the specific `actual` read
    that triggered a failure (committed `a3debf6`).
- `docs/CLI_CONTRACT.md`:
  - Newline-limitation docs rewritten for `type`/`submit-chat-message`; `screenshot` PrintWindow
    known-limitation note added (committed `dee237e`).
  - Phase 2 verb docs (4 new verbs) added/expanded; "Fixed 2026-09-15" notes on `attach`/`click`;
    `wait-for-process-responding` doc rewritten for foreground-preference semantics
    (committed `06850e6`).
  - Phase 14 known-limitation/mitigation entries added under `type` and `submit-chat-message`
    (committed `a3debf6`).
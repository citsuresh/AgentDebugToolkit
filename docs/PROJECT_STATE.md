# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

Phase 13 (generic `find-first`/`find-all` verbs, replacing `has-pending-prompt`) is implemented,
committed (`ae45478`), and pushed to `origin/main`. Design concern raised after Phase 11 shipped:
`has-pending-prompt` hardcoded Copilot-Chat-specific detection patterns (`Name=="Waiting..."`,
`AutomationId=="RadioFieldLabel"`, `ControlType.RadioButton`) directly into the CLI, which should
stay a generic UIA interface with app-specific patterns living in caller-side scripts/config.
Pre-Build Decomposition compared two options (parameterize `has-pending-prompt`'s three selectors
vs. deprecate it for fully generic `find-first`/`find-all` primitives); the user chose the latter.
`has-pending-prompt` removed entirely; `find-first --hwnd <h> --strategy <S> --value <V>
[--scopeStrategy <S> --scopeValue <V>]` (single `FindFirst`, optional narrower scope) and
`find-all` (same args plus `--excludeValue`, uses `FindAll`) added instead, reusing the existing
`Selector`/`SelectorStrategy` abstraction. Confirmed no existing caller depended on
`has-pending-prompt`'s shape: `tools/Watch-CopilotChat.ps1` never called it — it already does its
own client-side Name/AutomationId/ControlType filtering over a full `inspect` dump, so app-specific
logic already lived in the caller script, not the CLI. `docs/CLI_CONTRACT.md` updated: new
`find-first`/`find-all` sections (with a migration example reproducing the old `has-pending-prompt`
check), `has-pending-prompt` section removed, and `NameRegex`/`ControlTypeIndex`/`Coordinates` now
correctly documented as "not yet implemented" instead of a stale "(Phase 3)" tag. Build clean (0
warnings/errors). Live-validated `find-first`/`find-all` against the real VS Insiders window (hwnd
`0xCA18B2`): not-found, found, and scope-argument-validation cases. Independent Regression Audit
run; one finding (this file and `docs/full-graph.json` still referencing the removed verb) — user
explicitly chose to defer that to this End Session rather than a manual mid-task patch (now
addressed by this update/refresh).

Phase 12 (clipboard-paste fallback, `--paste` flag on `type`/`submit-chat-message`) is implemented,
committed (`380079d`), and pushed to `origin/main`. Adds `--paste` to both verbs: sets clipboard
text (via `System.Windows.Forms.Clipboard`) and sends `Ctrl+V` instead of slow per-character
synthetic keystrokes, for elements without `ValuePattern` support (notably the VS Copilot Chat
composer). No-op (falls back to `"pattern"`) when `ValuePattern` is available. Registers Clipboard
History / Cloud Clipboard opt-out marker formats on the pasted text only (never on restored
content), per the documented Windows opt-out mechanism. Full-fidelity clipboard snapshot/restore
via `IDataObject` (not text-only) preserves non-text formats that may have coexisted with text;
`clipboardRestored` is tri-state (`null` = nothing to restore, `true` = restored, `false` = restore
attempted and failed). Two real bugs were found and fixed during live validation (not just
code-review): (1) top-level statements do not imply `[STAThread]` — `System.Windows.Forms.Clipboard`
requires STA and threw `InvalidOperationException` until the entry point was wrapped to run on an
explicit STA thread when needed; (2) `Clipboard.GetDataObject()` returns a live COM wrapper tied to
the clipboard owner that goes stale once the clipboard is overwritten, silently no-opping the
restore despite reporting `clipboardRestored: true` — fixed by eagerly copying every format's data
into an owned `DataObject` before pasting. Live-validated against the real VS Copilot Chat composer
(paste, verify with line-ending normalization for RichEdit's `\n`→`\r` behavior, and a genuine
clipboard-restore round-trip using a known marker value) without ever submitting a message.
Independent Regression Audit run twice (once on the initial implementation, once on the two
audit-driven fixes); all in-scope findings addressed.

Phase 11 (`has-pending-prompt --hwnd <h>` verb) was implemented and committed (`41bf2de`) in an
earlier session, then **removed in Phase 13** (see above) after a design concern that it baked
app-specific detection patterns into the CLI. No functional capability was lost: the same check
can be reproduced by callers via `find-first`/`find-all` (documented in `CLI_CONTRACT.md`'s
migration note).

`send-keys` was independently live-validated (an earlier session) against a disposable, isolated
Notepad instance (not the live VS window) — sent literal text, verified via `get-text`, then sent
`^a{DEL}` and verified the field was cleared. No bug found.

Phase 9/10 (reliable interaction primitives; chat composer selector gap fix) remain implemented,
committed, and pushed — see git history (`4178912`, `94c5f15`) for details; unchanged this session.

## Open Tasks / Known Issues

- **Radio-button prompt detection path not independently re-validated since the Phase 13
  redesign.** The old `has-pending-prompt`'s `RadioFieldLabel`/`RadioButton` extraction logic was
  never independently live-validated against an actual radio-button-style confirmation card (only
  a freeform text-field card appeared during Phase 11's live testing) — and that logic no longer
  exists in the CLI at all post-Phase-13; a caller wanting it now composes `find-first`/`find-all`
  themselves. Worth validating opportunistically if/when a radio-button card next appears, using
  the caller-side approach documented in `CLI_CONTRACT.md`.
- `find-first`/`find-all`'s `--scopeStrategy`/`--scopeValue` narrowing was validated only for the
  argument-mismatch error case and the "no scope supplied" default-to-window-root case; the actual
  narrowed-scope-resolves-and-narrows-search path was not independently live-tested this session
  (code-reviewed only, matching the existing `ResolveSelector` pattern it reuses).
  - Enumerating radio-button *options* via `find-all` still needs a control-type-based match,
    which is not yet a supported `Selector` strategy (`Name`/`AutomationId` only today) — documented
    as a known gap in `CLI_CONTRACT.md`'s migration note, with `inspect` as the fallback.
- `type`'s embedded-newline rejection is unconditional across both its `ValuePattern` and
  synthetic-keyboard paths (skipped only when `--paste` is active) — a deliberate
  simplicity/uniformity tradeoff, not a fix pending.
- `screenshot`'s `PrintWindow`/`PW_RENDERFULLCONTENT` capture (Phase 8) can return an incomplete or
  incorrectly scaled frame for a non-foreground/non-visible window — proposed as a known-limitation
  doc item during Phase 9 breakdown but not yet written up.
- Validate Phase 7 exit criteria against `CAMFWDownloadConsole.exe`; current validation uses
  `cmd.exe` smoke targets only.
- Phase 2 UI Automation gaps remain: `wait-for-window-change`, `wait-for-process-responding`,
  `delay`, and `set-context` are documented but not dispatched; `--scopeHwnd` is ignored by
  `click`/`type`; and `attach --process <name>.exe` does not normalize the suffix. Explicitly out
  of scope unless directed otherwise.
- See `docs/KNOWN_OPEN_FINDINGS.md` for user-curated deferred findings (clipboard-unrelated:
  `SessionContext.Save()` file-move race, `JsonOutput` null-field omission, unimplemented selector
  strategies throwing uncaught exceptions, `DteLocator` COM object leaks, `nuget.config` scope).

## Recently Changed Files

- `src/AgentDebugToolkit.UiAutomation.Cli/Program.cs` (Phase 12: STA-thread entry-point wrapper;
  `--paste` support in `Verbs.Type`/`Verbs.SubmitChatMessage`; `NormalizeLineEndings`,
  `ElementSupportsValuePattern` helpers. Phase 13: removed `Verbs.HasPendingPrompt` and its
  dispatch case; added `Verbs.FindFirst`/`Verbs.FindAll`, `ResolveFindScope`, `ParseSelectorArgs`,
  `ToElementSummary`, and their dispatch cases)
- `src/AgentDebugToolkit.UiAutomation.Cli/UiaHelper.cs` (Phase 12: `ClipboardUnavailableException`,
  `TypeViaPaste`, `SetClipboardTextWithRetry`/`SetClipboardDataWithRetry`,
  `GetTextForPasteVerification`. Phase 13: added `ResolveSelectorAll`)
- `docs/CLI_CONTRACT.md` (Phase 12: `--paste` design/contract, STA-fix and stale-COM-wrapper-fix
  notes, tri-state `clipboardRestored`. Phase 13: `find-first`/`find-all` contract, `has-pending-prompt`
  section removed with migration note, `NameRegex`/`ControlTypeIndex`/`Coordinates` phase-tag fix)

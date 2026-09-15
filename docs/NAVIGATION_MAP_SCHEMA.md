# Navigation Map Schema (Agent-Owned)

**This file format is read/written entirely by the calling agent (Claude), using its own file
tools (view/create_file/replace_string_in_file). The CLI (`AgentDebugToolkit.UiAutomation.Cli`)
has no knowledge of this format and never reads or writes it.** This is a deliberate architecture
decision — see `ARCHITECTURE.md` point 1.

## Why a map is needed

The CLI only answers "what's on screen right now" (via `inspect`/`list-windows`) and "do this
action." It has no concept of "which screen am I on" or "how do I get from screen A to screen B."
The agent builds and maintains that knowledge itself, incrementally, as it explores the target
application — this file format is how that knowledge persists across sessions.

## File layout

```
docs/app-navigation-map/
  index.json              Small: list of {screenId, matchRule, filePath}. Always loaded.
  screens/
	login-screen.json       Full elements[]/transitions[] for one screen. Loaded on demand.
	main-tools-menu.json
	device-control-panel.json
	...
```

Rationale: `index.json` stays small even with hundreds of screens (only match rules, not full
element lists), so the agent can always load it cheaply to answer "which screen is this?" without
pulling in every screen's full detail. Per-screen files are loaded only when the agent is about to
act on that specific screen. This also keeps git diffs scoped to one screen at a time.

## index.json

```json
{
  "application": "Itron.Fdm.Mobile.SystemWorkflows.OpenWayCOSEM.PC",
  "screens": [
	{ "screenId": "login-screen", "file": "screens/login-screen.json",
	  "matchRule": { "titleRegex": "^Field Deployment Manager$",
		"requiredElementNames": ["OK"], "isModal": false } },
	{ "screenId": "main-tools-menu", "file": "screens/main-tools-menu.json",
	  "matchRule": { "titleRegex": "^Field Deployment Manager$",
		"requiredElementNames": ["Tools"], "requiredElementAbsent": ["OK"] } }
  ]
}
```

- `matchRule` must be enough for the agent to disambiguate between screens that share the same
  window title (very common in this app — the whole shell keeps the same title throughout).
  Prefer `requiredElementNames` (element presence/absence checks via `inspect`/`get-text`) over
  title alone.
- Append new entries; do not rewrite the whole file when adding a screen (reduces diff size /
  merge risk, consistent with other persistent-memory docs in this project family).

## screens/<screenId>.json

```json
{
  "screenId": "device-control-panel",
  "windowRelationship": "independent" | "modal" | "modeless",
  "parentScreenId": "main-tools-menu" | null,
  "transient": false,
  "elements": [
	{ "elementId": "addressField", "selector": { "strategy": "Name", "value": "10.176.100.248" },
	  "role": "input", "optional": false, "selectorConfidence": "high",
	  "description": "IP address text field (name shown is placeholder/example value; verify by
		get-text before relying on it)" },
	{ "elementId": "nextButton", "selector": { "strategy": "Name", "value": "Next" },
	  "role": "button", "optional": false, "selectorConfidence": "high" }
  ],
  "transitions": [
	{ "elementId": "nextButton", "action": "click", "targetScreenId": "device-progress-dialog",
	  "condition": "always" }
  ],
  "lastVerifiedSession": "2026-09-14",
  "notes": "Native WinForms EDIT/BUTTON/COMBOBOX controls present but no UIA patterns supported;
	use coordinate/synthetic-input path (see VALIDATION_FINDINGS.md)."
}
```

Field notes:
- `windowRelationship`: `"independent"` (own top-level window, not modal to anything),
  `"modal"` (blocks its owner), `"modeless"` (secondary window, can coexist). See earlier design
  discussion on multi-window handling.
- `transient`: true for screens like a "Loading..." dialog that should be waited *past*
  automatically rather than treated as a final destination.
- `optional`: true for elements that may not always be present/rendered (conditional UI).
- `selectorConfidence`: `"high"` (Name reliably unique and stable), `"medium"`, `"low"` (e.g.,
  coordinate-only fallback for owner-drawn controls with no usable Name) — informs the agent how
  much to trust a selector before falling back to a fresh `inspect`.
- `lastVerifiedSession`: date stamp of last confirmed-working use; if a selector fails, the agent
  should flag drift here rather than silently deleting the entry (append a note, don't overwrite
  history) — consistent with the append-only pattern used elsewhere in this project's docs.

## Screen identity rules (see prior scenario analysis for full rationale)

- Do **not** create a new screen node for purely visual/data-state differences (e.g., grid empty
  vs. populated). Only create a new node when the *set of reachable actions* differs.
- Tabs within one window are modeled as separate screen nodes with `parentScreenId` set and a
  `matchRule.requiredElementNames` check for the tab's unique content.
- Wizards (multi-step, same window) are a chain of screen nodes linked via `transitions`, not a
  single node.
- The graph is not a tree — multiple transitions from different source screens may point to the
  same `targetScreenId` (a screen can be reached multiple ways).
- Unrecognized windows (no matching `index.json` entry) are not an error — the agent should
  `inspect` them, decide if they're a new screen worth mapping (e.g., a real error dialog) or a
  one-off/transient state, and add an entry only if it's worth persisting.

## Progress dialogs / multi-step operations

Model the transient progress state as its own screen node with `"transient": true`. The agent's
wait loop (using CLI's `wait-for-window-change` / repeated `list-windows` + `inspect` checks
against the map) should treat a match against a transient screen as "keep waiting," only stopping
when a non-transient screen or a known alternate outcome (e.g., an error dialog) is matched.

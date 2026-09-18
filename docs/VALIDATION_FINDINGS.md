# Validation Findings

These findings come from a live UIA probe (PowerShell, `System.Windows.Automation`) run against
the actual running FDM Mobile app (`Itron.Fdm.Mobile.SystemWorkflows.OpenWayCOSEM.PC`, process
name `Fdm`) before any toolkit code was written. They establish the ground truth this toolkit's
design is based on. Update this file if future testing reveals different behavior in other
screens or other target applications.

## Summary

The app is a custom-drawn WinForms UI (not standard Win32/WinForms controls in most places).
UI Automation support is **inconsistent by screen** — some screens are fully custom-painted with
no invocable patterns, others contain genuine native WinForms controls with working patterns.
The toolkit must support both, with graceful fallback.

## Specific findings

0. **Owned native WPF MessageBox dialogs (validated 2026-09-18):**
   - A live WPF window displaying `MessageBox.Show(owner, ..., YesNo)` creates a visible native
     `#32770` window in the owner process. UIA's desktop `TreeScope.Children` query did not return
     that dialog, while Win32 `EnumWindows` did.
   - `agentdebug-ui list-windows --pid <pid>` now returns the dialog's HWND, title, `#32770` class,
     owner HWND, modal/foreground state, and bounds through Win32 enumeration.
   - The returned HWND worked with `inspect`, `get-text --strategy Name --value Yes`, and
     `click --strategy Name --value Yes`; the click invoked the button and closed the dialog.

1. **Menu/list screens (e.g., main Tools menu, device-type list, workflow step list):**
   - Every element reports `ControlType.Pane`, generic `ClassName`
	 (`WindowsForms10.Window.8.app.0...`).
   - `AutomationId` is a meaningless numeric runtime value (e.g., `11016060`), not derived from
	 designer `.Name` — **do not rely on AutomationId** as a primary selector for this app.
   - `Name` property reliably carries real, human-meaningful text (e.g., "OK", "Exit",
	 "1. Gen5 Riva Electricity Meter", "01. Device Control Panel").
   - No elements support `InvokePattern`/`TogglePattern`/`ValuePattern`.
   - **Verified working interaction:** locate element by `Name`, compute click point from
	 `BoundingRectangle` center, issue a synthetic mouse click (`SetCursorPos` +
	 `mouse_event`/`SendInput`). Confirmed working through two real navigation steps (login OK
	 button -> main Tools menu -> device-type menu -> Device Control Panel screen).

2. **Data-entry/settings screens (e.g., Device Control Panel / connection settings):**
   - Mixed: some elements are genuinely native WinForms controls (`WindowsForms10.EDIT`,
	 `WindowsForms10.BUTTON`, `WindowsForms10.COMBOBOX`) with real `ControlType.Edit` /
	 `ControlType.ComboBox`, but even these mostly report **no supported patterns** and numeric
	 AutomationIds, same as the custom screens.
   - One exception found: a ComboBox's internal dropdown-arrow `Button` sub-element supports
	 `InvokePattern`. This is inconsistent even within a single screen/control.
   - Conclusion: pattern support cannot be assumed present anywhere in this app. Treat it as an
	 optional fast-path, never a requirement.

3. **Not yet validated:** ComponentOne `C1FlexGrid`-based screens (grids/tables), found in ~19
   page classes in the source (e.g., `RegisterValuesPage`, `ConfigurationManagementPage`). These
   are a known risk area for poor UIA support (opaque/flattened structure, no per-cell identity)
   and should be spot-checked once Phase 1 tooling exists.

## Design implications

- **Primary selector strategy: `Name` match** (exact string, later extensible to regex), resolved
  live to a `BoundingRectangle` at the moment of action — not a cached coordinate, since layout
  can shift between app runs/window sizes.
- **Primary action mechanism: synthetic input** (`SendInput`/`mouse_event` for clicks,
  `SendInput` keyboard events for typing), not UIA `InvokePattern`/`ValuePattern`.
- **Opportunistic pattern use:** if `InvokePattern`/`ValuePattern`/`TogglePattern` happens to be
  supported on a resolved element, prefer it (faster, more reliable when available) — but the
  adapter must never *require* it.
- **`AutomationId` is unreliable for this app** and must not be the primary or default selector
  strategy, contrary to what a generic UIA tooling design would normally assume. It may still be
  useful for *other* target apps (e.g., genuine WPF apps with explicit `x:Name`/
  `AutomationProperties.AutomationId`), so the selector model should keep it as a supported
  strategy, just not assume it works everywhere.
- **Coordinates must be window-client-relative**, recomputed at action time from a live
  `BoundingRectangle` lookup by Name — never stored as absolute screen coordinates in the
  navigation map, since the window can move/resize between sessions.

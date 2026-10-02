using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AgentDebugToolkit.Mcp;

/// <summary>
/// One MCP tool per agentdebug-ui.exe verb, as documented in docs/CLI_CONTRACT.md's
/// "CLI Contract — AgentDebugToolkit.UiAutomation.Cli" section. Each tool only builds an
/// argument list and relays the CLI's own JSON output — no UIA/Win32 logic lives here.
/// </summary>
[McpServerToolType]
public sealed class UiAutomationTools
{
    private const string Exe = "agentdebug-ui.exe";
    private readonly CliRunner _cli;

    public UiAutomationTools(CliRunner cli)
    {
        _cli = cli;
    }

    private static List<string> BuildArgs(string verb, params (string Flag, string? Value)[] options)
    {
        var args = new List<string> { verb };
        foreach (var (flag, value) in options)
        {
            if (value is not null)
            {
                args.Add(flag);
                args.Add(value);
            }
        }
        return args;
    }

    private async Task<ToolInvocationResult> RunAsync(List<string> args, int? timeoutMs, CancellationToken ct)
    {
        var result = await _cli.RunAsync(Exe, args, timeoutMs, ct);
        return CliRunner.ToInvocationResult(result);
    }

    private static string? Bool(bool? value) => value is null ? null : (value.Value ? "true" : "false");

    [McpServerTool(Name = "ui_attach", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Resolves a running process by name (flexible, .exe suffix optional) and persists it as the session's target. Returns every visible top-level window owned by it. Does not itself read or change the target's UI state, but changes server-side session context.")]
    public Task<ToolInvocationResult> Attach(
        [Description("Process name to match (no .exe suffix assumed either way).")] string process,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("attach", ("--process", process)), null, cancellationToken);

    [McpServerTool(Name = "ui_list_windows", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns every visible top-level window belonging to the target process (session context pid, or an explicit pid). Read-only.")]
    public Task<ToolInvocationResult> ListWindows(
        [Description("Optional pid; uses the session context pid if omitted.")] int? pid = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("list-windows", ("--pid", pid?.ToString())), null, cancellationToken);

    [McpServerTool(Name = "ui_inspect", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Dumps the UIA subtree rooted at the given window/element handle, optionally saving a screenshot. Read-only (does not interact with the UI).")]
    public Task<ToolInvocationResult> Inspect(
        [Description("Window handle, e.g. '0x00123456'.")] string hwnd,
        [Description("Maximum tree depth (default implementation-defined, capped internally).")] int? maxDepth = null,
        [Description("Also save a screenshot of the window.")] bool? screenshot = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("inspect", ("--hwnd", hwnd), ("--maxDepth", maxDepth?.ToString()), ("--screenshot", Bool(screenshot))), null, cancellationToken);

    [McpServerTool(Name = "ui_click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Resolves an element via selector and clicks it (UIA pattern, falling back to a synthetic click). Changes target application state.")]
    public Task<ToolInvocationResult> Click(
        [Description("Window handle to resolve the selector against.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Optional narrower scope window/element handle to search within instead of hwnd.")] string? scopeHwnd = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("click", ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value), ("--scopeHwnd", scopeHwnd)), null, cancellationToken);

    [McpServerTool(Name = "ui_right_click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Resolves an element via selector and performs a synthetic right-click at its bounding-rect center (e.g. to open a context menu). Changes target application state.")]
    public Task<ToolInvocationResult> RightClick(
        [Description("Window handle to resolve the selector against.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Optional narrower scope window/element handle to search within instead of hwnd.")] string? scopeHwnd = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("right-click", ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value), ("--scopeHwnd", scopeHwnd)), null, cancellationToken);

    [McpServerTool(Name = "ui_double_click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Resolves an element via selector and double-clicks it (UIA pattern, falling back to two rapid synthetic clicks). Changes target application state.")]
    public Task<ToolInvocationResult> DoubleClick(
        [Description("Window handle to resolve the selector against.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Optional narrower scope window/element handle to search within instead of hwnd.")] string? scopeHwnd = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("double-click", ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value), ("--scopeHwnd", scopeHwnd)), null, cancellationToken);

    [McpServerTool(Name = "ui_drag", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Resolves a source element and performs a synthetic mouse drag to either another selector-resolved element, or explicit screen coordinates. Changes target application state.")]
    public Task<ToolInvocationResult> Drag(
        [Description("Window handle to resolve the source selector against.")] string hwnd,
        [Description("Source selector strategy: Name or AutomationId.")] string strategy,
        [Description("Source selector value to match.")] string value,
        [Description("Target selector strategy. Mutually exclusive with targetX/targetY; exactly one destination form must be given.")] string? targetStrategy = null,
        [Description("Target selector value. Required together with targetStrategy.")] string? targetValue = null,
        [Description("Target absolute screen X coordinate. Mutually exclusive with targetStrategy/targetValue.")] int? targetX = null,
        [Description("Target absolute screen Y coordinate. Required together with targetX.")] int? targetY = null,
        [Description("Optional narrower scope window/element handle to search within instead of hwnd.")] string? scopeHwnd = null,
        [Description("Number of interpolated intermediate mouse-move points (default 15, max 1000).")] int? steps = null,
        [Description("Total drag duration in milliseconds (default 300).")] int? durationMs = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "drag",
            ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value),
            ("--targetStrategy", targetStrategy), ("--targetValue", targetValue),
            ("--targetX", targetX?.ToString()), ("--targetY", targetY?.ToString()),
            ("--scopeHwnd", scopeHwnd), ("--steps", steps?.ToString()), ("--durationMs", durationMs?.ToString())), null, cancellationToken);

    [McpServerTool(Name = "ui_move_mouse", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Moves the cursor to an absolute screen position or a selector-resolved element's center, without pressing any mouse button. Minor environment change (cursor position) but no application-state side effect by itself.")]
    public Task<ToolInvocationResult> MoveMouse(
        [Description("Window handle to resolve the selector against. Required when using strategy/value; omit for explicit x/y.")] string? hwnd = null,
        [Description("Selector strategy: Name or AutomationId. Mutually exclusive with x/y.")] string? strategy = null,
        [Description("Selector value to match. Required together with strategy.")] string? value = null,
        [Description("Explicit absolute screen X coordinate. Mutually exclusive with strategy/value.")] int? x = null,
        [Description("Explicit absolute screen Y coordinate. Required together with x.")] int? y = null,
        [Description("Optional narrower scope window/element handle to search within instead of hwnd.")] string? scopeHwnd = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "move-mouse",
            ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value),
            ("--x", x?.ToString()), ("--y", y?.ToString()), ("--scopeHwnd", scopeHwnd)), null, cancellationToken);

    [McpServerTool(Name = "ui_get_cursor_pos", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns the current cursor position. Read-only, no arguments.")]
    public Task<ToolInvocationResult> GetCursorPos(CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("get-cursor-pos"), null, cancellationToken);

    [McpServerTool(Name = "ui_type", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Resolves an element and types text into it (ValuePattern, falling back to click-to-focus plus synthetic keyboard input, or clipboard paste). When ValuePattern is used (method: \"pattern\"), this REPLACES the control's entire existing value and typically leaves the caret at position 0, not at the end. To append instead, use ui_send_keys (send ^{END} first to move the caret to the end). Changes target application state.")]
    public Task<ToolInvocationResult> Type(
        [Description("Window handle to resolve the selector against.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Text to type.")] string text,
        [Description("Optional narrower scope window/element handle to search within instead of hwnd.")] string? scopeHwnd = null,
        [Description("Read back the typed text and fail with verify-mismatch/verify-unstable if it doesn't match (only checked when method is pattern or clipboard-paste).")] bool? verify = null,
        [Description("Use clipboard paste instead of per-character synthetic keystrokes (required for embedded newlines on controls with no ValuePattern).")] bool? paste = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "type",
            ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value), ("--text", text),
            ("--scopeHwnd", scopeHwnd), ("--verify", Bool(verify)), ("--paste", Bool(paste))), null, cancellationToken);

    [McpServerTool(Name = "ui_get_text", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Reads the current Name or ValuePattern.Value of the resolved element. Read-only.")]
    public Task<ToolInvocationResult> GetText(
        [Description("Window handle to resolve the selector against.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("get-text", ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value)), null, cancellationToken);

    [McpServerTool(Name = "ui_set_grid_cell", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Edits a cell in a UIA DataGrid (locates grid/row/column, scrolls virtualized grids into view, edits via ValuePattern/TogglePattern or synthetic input, optionally invokes an Apply action). Changes target application state.")]
    public Task<ToolInvocationResult> SetGridCell(
        [Description("Window handle containing the grid.")] string hwnd,
        [Description("Grid selector strategy: Name or AutomationId.")] string gridStrategy,
        [Description("Grid selector value.")] string gridValue,
        [Description("Row selector strategy: Name or AutomationId, matched against a descendant within the row.")] string rowStrategy,
        [Description("Row selector value.")] string rowValue,
        [Description("0-based column index (GridItemPattern column) of the target cell.")] int columnIndex,
        [Description("Text to set in the cell. For CheckBox editors, must be 'true' or 'false'.")] string text,
        [Description("Editor control type to expect: Edit (default), ComboBox, or CheckBox.")] string? editorControlType = null,
        [Description("Optional action element strategy to invoke after editing (e.g. a per-row Apply button). Must be supplied together with applyValue.")] string? applyStrategy = null,
        [Description("Optional action element value. Required together with applyStrategy.")] string? applyValue = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "set-grid-cell",
            ("--hwnd", hwnd), ("--gridStrategy", gridStrategy), ("--gridValue", gridValue),
            ("--rowStrategy", rowStrategy), ("--rowValue", rowValue), ("--columnIndex", columnIndex.ToString()),
            ("--text", text), ("--editorControlType", editorControlType),
            ("--applyStrategy", applyStrategy), ("--applyValue", applyValue)), null, cancellationToken);

    [McpServerTool(Name = "ui_wait_for_window_change", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Polls the target process's window set until it is stable for settleMs milliseconds, or timeoutMs elapses. Read-only (observational), but blocks for up to timeoutMs. Optional pid; uses the session context pid if omitted, matching ui_list_windows.")]
    public Task<ToolInvocationResult> WaitForWindowChange(
        [Description("Maximum time to wait, in milliseconds. Capped at 120000 for this tool.")] int timeoutMs,
        [Description("Optional pid of the target process; uses the session context pid if omitted.")] int? pid = null,
        [Description("Consecutive milliseconds the window set must be unchanged to be considered settled (default 300).")] int? settleMs = null,
        CancellationToken cancellationToken = default)
    {
        const int MaxWaitMs = 120_000;
        var cappedTimeoutMs = Math.Clamp(timeoutMs, 0, MaxWaitMs);
        var args = BuildArgs("wait-for-window-change", ("--pid", pid?.ToString()), ("--timeoutMs", cappedTimeoutMs.ToString()), ("--settleMs", settleMs?.ToString()));
        return RunAsync(args, cappedTimeoutMs + 10_000, cancellationToken);
    }

    [McpServerTool(Name = "ui_wait_for_element", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Resolves the target window once, then polls a selector within it until it reaches the requested state (appeared/disappeared/enabled) or timeoutMs elapses. Read-only (observational), but blocks for up to timeoutMs.")]
    public Task<ToolInvocationResult> WaitForElement(
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Window handle to resolve against. Mutually exclusive with pid; if neither is given, the session context window is used.")] string? hwnd = null,
        [Description("PID to resolve a window from. Mutually exclusive with hwnd.")] int? pid = null,
        [Description("Target state to wait for: appeared (default), disappeared, or enabled.")] string? state = null,
        [Description("Maximum time to wait, in milliseconds (default 5000). Capped at 120000 for this tool.")] int? timeoutMs = null,
        [Description("Poll interval in milliseconds (default 250).")] int? pollMs = null,
        CancellationToken cancellationToken = default)
    {
        const int MaxWaitMs = 120_000;
        var cappedTimeoutMs = timeoutMs is null ? (int?)null : Math.Clamp(timeoutMs.Value, 0, MaxWaitMs);
        var args = BuildArgs(
            "wait-for-element",
            ("--strategy", strategy), ("--value", value), ("--hwnd", hwnd), ("--pid", pid?.ToString()),
            ("--state", state), ("--timeoutMs", cappedTimeoutMs?.ToString()), ("--pollMs", pollMs?.ToString()));
        return RunAsync(args, (cappedTimeoutMs ?? 5000) + 10_000, cancellationToken);
    }

    [McpServerTool(Name = "ui_wait_for_process_responding", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Polls the target process's main window until it responds to SendMessageTimeout, or timeoutMs elapses. Read-only (observational), but blocks for up to timeoutMs.")]
    public Task<ToolInvocationResult> WaitForProcessResponding(
        [Description("PID of the target process.")] int pid,
        [Description("Maximum time to wait, in milliseconds. Capped at 120000 for this tool.")] int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        const int MaxWaitMs = 120_000;
        var cappedTimeoutMs = Math.Clamp(timeoutMs, 0, MaxWaitMs);
        var args = BuildArgs("wait-for-process-responding", ("--pid", pid.ToString()), ("--timeoutMs", cappedTimeoutMs.ToString()));
        return RunAsync(args, cappedTimeoutMs + 10_000, cancellationToken);
    }

    [McpServerTool(Name = "ui_delay", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Plain Thread.Sleep for the given milliseconds. No application-state side effect; blocks the caller.")]
    public Task<ToolInvocationResult> Delay(
        [Description("Milliseconds to sleep.")] int ms,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("delay", ("--ms", ms.ToString())), Math.Clamp(ms, 1, 120_000) + 5_000, cancellationToken);

    [McpServerTool(Name = "ui_set_context", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Manually persists a pid as the current session-context target process, without a by-name lookup. Changes server-side session context only, not any application state.")]
    public Task<ToolInvocationResult> SetContext(
        [Description("PID to persist as the current target process.")] int pid,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("set-context", ("--pid", pid.ToString())), null, cancellationToken);

    [McpServerTool(Name = "ui_read_visible_text", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Traverses the UIA subtree rooted at the given window and returns de-duplicated visible text (TextPattern/ValuePattern/Name). Never activates/focuses/clicks/types. Strictly read-only.")]
    public Task<ToolInvocationResult> ReadVisibleText(
        [Description("Window handle to read from.")] string hwnd,
        [Description("Maximum traversal depth (default 8).")] int? maxDepth = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("read-visible-text", ("--hwnd", hwnd), ("--maxDepth", maxDepth?.ToString())), null, cancellationToken);

    [McpServerTool(Name = "ui_screenshot", ReadOnly = false, Destructive = false, OpenWorld = false),
     Description("Captures a bitmap of the given window (PrintWindow with a CopyFromScreen fallback) and saves it as a PNG file on disk, returning the saved path. Does not activate/focus the window or change target application state, but is NOT read-only: it writes a new file to disk.")]
    public Task<ToolInvocationResult> Screenshot(
        [Description("Window handle to capture.")] string hwnd,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("screenshot", ("--hwnd", hwnd)), null, cancellationToken);

    [McpServerTool(Name = "ui_activate", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Brings the given window to the foreground (SetForegroundWindow). Unlike read-only verbs, this changes window activation, z-order, and input focus. Risk: can steal focus from whatever the user is currently doing.")]
    public Task<ToolInvocationResult> Activate(
        [Description("Window handle to bring to the foreground. No fallback to session context/pid.")] string hwnd,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("activate", ("--hwnd", hwnd)), null, cancellationToken);

    [McpServerTool(Name = "ui_send_keys", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Sends raw, unescaped SendKeys syntax (e.g. '^a', '{DELETE}', '{ENTER}', '^{END}') to the resolved element at its CURRENT caret position. focusMode set-focus only establishes focus and does not move the caret; click performs a synthetic click that may reposition the caret depending on the control (unverified). To append after ui_type (which can leave the caret at position 0), send ^{END} first. Newlines from {ENTER} may read back as \"\\r\" in RichEdit-style controls. Changes target application state; can trigger unintended actions if the syntax is wrong for the control.")]
    public Task<ToolInvocationResult> SendKeys(
        [Description("Window handle to resolve the selector against.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Unescaped SendKeys.SendWait syntax, e.g. '^a' for Ctrl+A, '{DELETE}', '{ENTER}'.")] string keys,
        [Description("How to establish focus before sending keys: set-focus (default), click, or none.")] string? focusMode = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "send-keys",
            ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value), ("--keys", keys), ("--focusMode", focusMode)), null, cancellationToken);

    [McpServerTool(Name = "ui_submit_chat_message", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Composite verb for the Copilot Chat input pattern: clicks the input, types text, verifies it landed, then submits (Send button click or SendKeys). Changes target application state and can trigger a real chat submission — treat as high-impact.")]
    public Task<ToolInvocationResult> SubmitChatMessage(
        [Description("Window handle containing the chat input.")] string hwnd,
        [Description("Text to type and submit.")] string text,
        [Description("Resolve the input by AutomationId. Mutually exclusive with inputStrategy/inputValue; exactly one input-selection mode must be given.")] string? inputAutomationId = null,
        [Description("Resolve the input by selector strategy (typically 'Name'). Must be supplied together with inputValue.")] string? inputStrategy = null,
        [Description("Selector value for inputStrategy, e.g. the placeholder Name shown when the input is empty.")] string? inputValue = null,
        [Description("Resolve a Send button by AutomationId and click it. Mutually exclusive with submitKeys.")] string? sendAutomationId = null,
        [Description("Unescaped SendKeys syntax sent to the input to submit instead of a Send button (default '{ENTER}' if neither this nor sendAutomationId is given).")] string? submitKeys = null,
        [Description("Use clipboard paste instead of synthetic keystrokes (required for embedded newlines when the input has no ValuePattern).")] bool? paste = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "submit-chat-message",
            ("--hwnd", hwnd), ("--text", text),
            ("--inputAutomationId", inputAutomationId), ("--inputStrategy", inputStrategy), ("--inputValue", inputValue),
            ("--sendAutomationId", sendAutomationId), ("--submitKeys", submitKeys), ("--paste", Bool(paste))), null, cancellationToken);

    [McpServerTool(Name = "ui_find_first", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Single shallow-cost FindFirst lookup against the resolved window or a narrower scope. Returns found=false (not an error) when nothing matches. Read-only.")]
    public Task<ToolInvocationResult> FindFirst(
        [Description("Window handle to search within.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Optional scope selector strategy to narrow the search to a descendant subtree first. Must be supplied together with scopeValue.")] string? scopeStrategy = null,
        [Description("Optional scope selector value. Required together with scopeStrategy.")] string? scopeValue = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "find-first",
            ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value),
            ("--scopeStrategy", scopeStrategy), ("--scopeValue", scopeValue)), null, cancellationToken);

    [McpServerTool(Name = "ui_find_all", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Same scoping/strategy support as ui_find_first, but returns every matching descendant via FindAll. Read-only.")]
    public Task<ToolInvocationResult> FindAll(
        [Description("Window handle to search within.")] string hwnd,
        [Description("Selector strategy: Name or AutomationId.")] string strategy,
        [Description("Selector value to match.")] string value,
        [Description("Optional scope selector strategy to narrow the search to a descendant subtree first. Must be supplied together with scopeValue.")] string? scopeStrategy = null,
        [Description("Optional scope selector value. Required together with scopeStrategy.")] string? scopeValue = null,
        [Description("Skip any matched element whose Name equals this value (e.g. to filter out a catch-all placeholder option).")] string? excludeValue = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "find-all",
            ("--hwnd", hwnd), ("--strategy", strategy), ("--value", value),
            ("--scopeStrategy", scopeStrategy), ("--scopeValue", scopeValue), ("--excludeValue", excludeValue)), null, cancellationToken);
}

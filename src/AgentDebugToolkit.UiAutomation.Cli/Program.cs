using System.Diagnostics;
using System.Windows.Automation;
using AgentDebugToolkit.Core;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.UiAutomation.Cli;

if (args.Length == 0)
{
    JsonOutput.WriteError("invalid-argument", "No verb specified. See docs/CLI_CONTRACT.md.");
    return 1;
}

var verb = args[0];
var opts = ParseOptions(args.Skip(1).ToArray());

try
{
    switch (verb)
    {
        case "attach":
            return Verbs.Attach(opts);
        case "list-windows":
            return Verbs.ListWindows(opts);
        case "inspect":
            return Verbs.Inspect(opts);
        case "click":
            return Verbs.Click(opts);
        case "type":
            return Verbs.Type(opts);
        case "get-text":
            return Verbs.GetText(opts);
        case "wait-for-element":
            return Verbs.WaitForElement(opts);
        case "read-visible-text":
            return Verbs.ReadVisibleText(opts);
        case "screenshot":
            return Verbs.Screenshot(opts);
        case "activate":
            return Verbs.Activate(opts);
        case "send-keys":
            return Verbs.SendKeys(opts);
        case "submit-chat-message":
            return Verbs.SubmitChatMessage(opts);
        default:
            JsonOutput.WriteError("invalid-argument", $"Unknown verb '{verb}'.");
            return 1;
    }
}
catch (Exception ex)
{
    JsonOutput.WriteError("unhandled-exception", ex.Message);
    return 1;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--"))
        {
            continue;
        }

        var key = args[i][2..];
        var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
        dict[key] = value;
    }
    return dict;
}

internal static class Verbs
{
    public static int Attach(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("process", out var processName))
        {
            JsonOutput.WriteError("invalid-argument", "--process is required.");
            return 1;
        }

        var candidates = Process.GetProcesses()
            .Where(p => p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            JsonOutput.WriteError("process-not-found", $"No running process named '{processName}'.");
            return 1;
        }

        if (candidates.Count > 1)
        {
            JsonOutput.WriteError(
                "ambiguous-process",
                $"Multiple processes named '{processName}' found.",
                new { candidates = candidates.Select(p => new { pid = p.Id }) });
            return 1;
        }

        var proc = candidates[0];
        SessionContext.Save(proc.Id, proc.ProcessName, proc.StartTime.ToUniversalTime());
        var windows = UiaHelper.ListTopLevelWindows(proc.Id);
        JsonOutput.WriteSuccess(new { pid = proc.Id, processName = proc.ProcessName, windows });
        return 0;
    }

    public static int ListWindows(Dictionary<string, string> opts)
    {
        var (pid, errorCode, error) = ResolveValidatedPid(opts);
        if (pid is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var windows = UiaHelper.ListTopLevelWindows(pid.Value);
        JsonOutput.WriteSuccess(new { windows });
        return 0;
    }

    public static int Inspect(Dictionary<string, string> opts)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        var element = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        if (element is null)
        {
            JsonOutput.WriteError("element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.");
            return 1;
        }

        var maxDepth = opts.TryGetValue("maxDepth", out var mdText) && int.TryParse(mdText, out var md) ? md : 8;
        var takeScreenshot = !opts.TryGetValue("screenshot", out var ssText) || ssText != "false";

        var tree = UiaHelper.ToElementInfo(element, includeChildren: true, maxDepth: maxDepth);
        string? screenshotPath = takeScreenshot ? ScreenshotHelper.Capture(element) : null;

        JsonOutput.WriteSuccess(new { root = tree, screenshotPath });
        return 0;
    }

    public static int Click(Dictionary<string, string> opts)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var (element, elementErrorCode, elementError) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!);
            return 1;
        }

        // Capture elementFound BEFORE invoking the click, not after: UiaHelper.Click() can change
        // the element's state (e.g. IsEnabled, IsOffscreen, or even cause it to disappear as a
        // direct result of the click), so reading it afterward would report the click's
        // aftereffect rather than what was actually clicked. This is a call-ordering fix, not a
        // timing/sleep fix — no delay is introduced, only the read is moved before the action.
        var info = UiaHelper.ToElementInfo(element, includeChildren: false, maxDepth: 0);
        var method = UiaHelper.Click(element);
        JsonOutput.WriteSuccess(new { method, elementFound = info });
        return 0;
    }

    public static int Type(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("text", out var text))
        {
            JsonOutput.WriteError("invalid-argument", "--text is required.");
            return 1;
        }

        // Reject embedded newlines: a raw '\n'/'\r' passed through to SendKeys.SendWait is not
        // treated as literal text — it can trigger unintended UI navigation (e.g. moving focus to
        // a different control), which was observed directly during Phase 9 validation rather than
        // being a theoretical concern. Rejecting up front is safer than silently mangling input or
        // producing surprising side effects.
        if (text.Contains('\n') || text.Contains('\r'))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "--text must not contain embedded newline characters ('\\n'/'\\r'); these are not " +
                "treated as literal text by the underlying SendKeys mechanism and can trigger " +
                "unintended UI navigation instead.");
            return 1;
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var (element, elementErrorCode, elementError) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!);
            return 1;
        }

        var method = UiaHelper.Type(element, text);

        // Optional read-back verification: --verify (matching --screenshot's boolean-flag
        // convention: absent or "false" disables it, any other value enables it) re-reads the
        // element's text after typing and fails with verify-mismatch if it doesn't match what was
        // sent. This exists to catch silent typing failures (e.g. a control that ignored or
        // truncated the input) that the plain success response cannot detect on its own.
        //
        // Verification is only meaningful when Type used ValuePattern.SetValue ("pattern"): in
        // that case GetText also reads back via ValuePattern, so both sides reflect the control's
        // actual value. When Type fell back to synthetic-keyboard input ("synthetic-keyboard",
        // i.e. no ValuePattern support), GetText falls back to the element's accessibility Name
        // (a static label), not its typed content — comparing that against the typed text would
        // almost always report a false-positive mismatch, not a genuine typing failure. Skip
        // verification for that case rather than produce an unreliable result.
        if (opts.TryGetValue("verify", out var verifyText) && verifyText != "false" && method == "pattern")
        {
            var actual = UiaHelper.GetText(element);
            if (actual != text)
            {
                JsonOutput.WriteError(
                    "verify-mismatch",
                    "The element's text after typing did not match the input.",
                    new { expected = text, actual });
                return 1;
            }
        }

        JsonOutput.WriteSuccess(new { method });
        return 0;
    }

    /// <summary>
    /// Companion to Type for key combinations Type cannot express: Type's underlying SendText
    /// always escapes SendKeys special characters so literal text is never misinterpreted as
    /// SendKeys syntax, which means Type has no way to send e.g. Ctrl+A, Delete, or Enter as key
    /// presses. send-keys instead accepts raw/unescaped SendKeys syntax via --keys.
    /// </summary>
    public static int SendKeys(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("keys", out var keys) || keys.Length == 0)
        {
            JsonOutput.WriteError("invalid-argument", "--keys is required and must be non-empty.");
            return 1;
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var (element, elementErrorCode, elementError) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!);
            return 1;
        }

        try
        {
            UiaHelper.SendKeys(element, keys);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            // SendKeys.SendWait throws for malformed syntax (e.g. an unbalanced "{" or an
            // unrecognized key name like "{FOO}") — surface this as a clean invalid-argument
            // instead of letting it propagate to the top-level unhandled-exception handler.
            JsonOutput.WriteError("invalid-argument", $"Invalid --keys syntax '{keys}': {ex.Message}");
            return 1;
        }

        JsonOutput.WriteSuccess(new { sent = true });
        return 0;
    }

    public static int GetText(Dictionary<string, string> opts)
    {
        var (scope, errorCode, error) = ResolveWindow(opts);
        if (scope is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var (element, elementErrorCode, elementError) = ResolveElement(scope, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!);
            return 1;
        }

        var text = UiaHelper.GetText(element);
        JsonOutput.WriteSuccess(new { text });
        return 0;
    }

    public static int WaitForElement(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("strategy", out var strategyText)
            || !Enum.TryParse<SelectorStrategy>(strategyText, ignoreCase: true, out var strategy))
        {
            JsonOutput.WriteError("invalid-argument", "--strategy is required and must be one of: Name, AutomationId.");
            return 1;
        }

        if (!opts.TryGetValue("value", out var value))
        {
            JsonOutput.WriteError("invalid-argument", "--value is required.");
            return 1;
        }

        var state = "appeared";
        if (opts.TryGetValue("state", out var stateText))
        {
            if (stateText is not ("appeared" or "disappeared" or "enabled"))
            {
                JsonOutput.WriteError(
                    "invalid-argument", "--state must be one of: appeared, disappeared, enabled.");
                return 1;
            }

            state = stateText;
        }

        if (opts.TryGetValue("timeoutMs", out var tText)
            && (!int.TryParse(tText, out var parsedTimeoutMs) || parsedTimeoutMs < 0))
        {
            JsonOutput.WriteError("invalid-argument", "--timeoutMs must be a non-negative integer.");
            return 1;
        }

        var timeoutMs = opts.TryGetValue("timeoutMs", out var t2) ? int.Parse(t2) : 5000;

        if (opts.TryGetValue("pollMs", out var pText)
            && (!int.TryParse(pText, out var parsedPollMs) || parsedPollMs <= 0))
        {
            JsonOutput.WriteError("invalid-argument", "--pollMs must be a positive integer.");
            return 1;
        }

        var pollMs = opts.TryGetValue("pollMs", out var p2) ? int.Parse(p2) : 250;

        // Resolve the target window once up front, per click/type conventions: a resolution
        // failure here (bad --hwnd/--pid, no session context, ambiguous window) is not
        // transient and should fail immediately rather than being retried across the poll loop.
        var (scope, scopeErrorCode, scopeError) = ResolveWindow(opts);
        if (scope is null)
        {
            JsonOutput.WriteError(scopeErrorCode!, scopeError!);
            return 1;
        }

        var windowHwnd = (IntPtr)scope.Current.NativeWindowHandle;
        var selector = new Selector { Strategy = strategy, Value = value };
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            // For "disappeared", the window itself may have closed; re-check its liveness each
            // tick. For "appeared"/"enabled" the window is expected to persist, so reuse the
            // scope resolved above rather than re-walking the desktop every poll.
            if (state == "disappeared" && !UiaHelper.IsWindowAlive(windowHwnd))
            {
                JsonOutput.WriteSuccess(
                    new { elementFound = (object?)null, elapsedMs = stopwatch.ElapsedMilliseconds },
                    preserveNullFields: true);
                return 0;
            }

            AutomationElement? element = null;
            if (UiaHelper.IsWindowAlive(windowHwnd))
            {
                element = UiaHelper.ResolveSelector(scope, selector);
            }

            var satisfied = state switch
            {
                "appeared" => element is not null,
                "disappeared" => element is null,
                "enabled" => element is not null && element.Current.IsEnabled,
                _ => false
            };

            if (satisfied)
            {
                var info = element is not null
                    ? UiaHelper.ToElementInfo(element, includeChildren: false, maxDepth: 0)
                    : null;
                JsonOutput.WriteSuccess(
                    new { elementFound = info, elapsedMs = stopwatch.ElapsedMilliseconds },
                    preserveNullFields: true);
                return 0;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                JsonOutput.WriteError(
                    "timeout",
                    $"Element [{strategy}] '{value}' did not reach state '{state}' within {timeoutMs}ms.");
                return 1;
            }

            Thread.Sleep(pollMs);
        }
    }

    public static int ReadVisibleText(Dictionary<string, string> opts)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        // Read-only, but a hung target's UIA calls (FindAll/pattern property reads) can still
        // block on its message pump, so fail fast the same way click/type do rather than hanging
        // indefinitely on an unresponsive window.
        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var element = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        if (element is null)
        {
            JsonOutput.WriteError("element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.");
            return 1;
        }

        int? maxDepth = null;
        if (opts.TryGetValue("maxDepth", out var mdText))
        {
            if (!int.TryParse(mdText, out var md) || md < 0)
            {
                JsonOutput.WriteError("invalid-argument", "--maxDepth must be a non-negative integer.");
                return 1;
            }
            maxDepth = md;
        }

        var (lines, truncated) = UiaHelper.CollectVisibleText(element, maxDepth);
        JsonOutput.WriteSuccess(new { lines, truncated });
        return 0;
    }

    public static int Screenshot(Dictionary<string, string> opts)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        // GetWindowRect itself is a cheap, direct query of cached window-manager state (not a
        // cross-process SendMessage), so it won't hang on a hung target. The IsResponding check
        // here instead guards CopyFromScreen's pixel data quality: a hung/non-repainting window
        // can be stale or show a "not responding" ghost overlay, which would silently produce a
        // misleading screenshot rather than a clear error. Kept consistent with read-visible-text.
        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        string screenshotPath;
        try
        {
            screenshotPath = ScreenshotHelper.CaptureWindow(windowHwnd);
        }
        catch (InvalidOperationException ex)
        {
            // The hwnd resolved successfully above, but GetWindowRect failed at capture time —
            // most plausibly because the window closed in the gap between resolution and
            // capture. This is a "the target went away" condition, not a "selector picked the
            // wrong thing" condition, so it uses stale-context rather than element-not-found.
            JsonOutput.WriteError("stale-context", ex.Message);
            return 1;
        }

        JsonOutput.WriteSuccess(new { screenshotPath });
        return 0;
    }

    /// <summary>
    /// Brings a window to the foreground. Unlike every Phase 8 verb, this is intentionally
    /// interactive/non-read-only: it changes window activation/z-order/focus state rather than
    /// only observing it.
    /// </summary>
    public static int Activate(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("hwnd", out var hwndText))
        {
            JsonOutput.WriteError("invalid-argument", "--hwnd is required.");
            return 1;
        }

        IntPtr windowHwnd;
        try
        {
            windowHwnd = UiaHelper.ParseHwnd(hwndText);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // ArgumentException (and its ArgumentOutOfRangeException subclass) covers empty or
            // "0x"-only input: ParseHwnd's underlying Convert.ToInt64 throws
            // ArgumentOutOfRangeException for an empty string rather than FormatException.
            JsonOutput.WriteError("invalid-argument", $"Invalid hwnd '{hwndText}'.");
            return 1;
        }

        // SetForegroundWindow can legitimately return false
        // window: Windows' focus-stealing prevention denies the foreground switch depending on
        // which process last had input focus (e.g. the calling process isn't the foreground
        // process and doesn't hold input for the target's thread). This is not the same as the
        // window having closed, but both surface as a "could not activate" outcome here; treat it
        // as stale-context (matching screenshot's usage for "target no longer usable as
        // resolved") rather than a fatal error — a caller should not treat this as fatal and may
        // retry or fall back to manual activation.
        var activated = NativeMethods.SetForegroundWindow(windowHwnd);
        if (!activated)
        {
            JsonOutput.WriteError(
                "stale-context",
                $"Could not bring hwnd '0x{windowHwnd.ToInt64():X}' to the foreground. The window " +
                "may have closed, or Windows denied the foreground switch (focus-stealing " +
                "prevention) — this is not necessarily fatal; retry or activate manually.");
            return 1;
        }

        JsonOutput.WriteSuccess(new { activated = true });
        return 0;
    }

    /// <summary>
    /// Composite verb tailored to the Copilot Chat input pattern: click the input, type the text,
    /// verify it landed via read-back, then submit it — as a single call instead of the
    /// click/type/submit dance a caller would otherwise have to script themselves. It preserves
    /// AutomationId selection for compatible chat UIs and also supports the Name exposed by the
    /// real Visual Studio Copilot Chat composer while it is empty.
    /// </summary>
    public static int SubmitChatMessage(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("text", out var text))
        {
            JsonOutput.WriteError("invalid-argument", "--text is required.", new { step = "validate-arguments" });
            return 1;
        }

        // Same embedded-newline guard as the standalone type verb (this composite verb calls
        // UiaHelper.Type directly rather than Verbs.Type, so the check is duplicated here rather
        // than inherited).
        if (text.Contains('\n') || text.Contains('\r'))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "--text must not contain embedded newline characters ('\\n'/'\\r'); these are not " +
                "treated as literal text by the underlying SendKeys mechanism and can trigger " +
                "unintended UI navigation instead.",
                new { step = "validate-arguments" });
            return 1;
        }

        var hasInputAutomationId = opts.TryGetValue("inputAutomationId", out var inputAutomationId);
        var hasInputStrategy = opts.TryGetValue("inputStrategy", out var inputStrategy);
        var hasInputValue = opts.TryGetValue("inputValue", out var inputValue);
        if (hasInputAutomationId && inputAutomationId!.Length == 0)
        {
            JsonOutput.WriteError(
                "invalid-argument", "--inputAutomationId is required and must be non-empty.",
                new { step = "validate-arguments" });
            return 1;
        }

        if (hasInputAutomationId && (hasInputStrategy || hasInputValue))
        {
            JsonOutput.WriteError(
                "invalid-argument", "Specify either --inputAutomationId or --inputStrategy with --inputValue, not both.",
                new { step = "validate-arguments" });
            return 1;
        }

        if (!hasInputAutomationId
            && (!hasInputStrategy || !hasInputValue
                || !inputStrategy!.Equals("Name", StringComparison.OrdinalIgnoreCase)
                || inputValue!.Length == 0))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "Specify --inputAutomationId, or --inputStrategy Name with a non-empty --inputValue.",
                new { step = "validate-arguments" });
            return 1;
        }

        var hasSendAutomationId = opts.TryGetValue("sendAutomationId", out var sendAutomationId);
        var hasSubmitKeys = opts.TryGetValue("submitKeys", out var submitKeys);
        if (hasSendAutomationId && sendAutomationId!.Length == 0)
        {
            JsonOutput.WriteError(
                "invalid-argument", "--sendAutomationId must be non-empty.",
                new { step = "validate-arguments" });
            return 1;
        }

        if (hasSendAutomationId && hasSubmitKeys)
        {
            JsonOutput.WriteError(
                "invalid-argument", "Specify either --sendAutomationId or --submitKeys, not both.",
                new { step = "validate-arguments" });
            return 1;
        }

        if (hasSubmitKeys && submitKeys!.Length == 0)
        {
            JsonOutput.WriteError(
                "invalid-argument", "--submitKeys must be non-empty.",
                new { step = "validate-arguments" });
            return 1;
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!, new { step = "resolve-window" });
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            JsonOutput.WriteError(
                "window-not-responding", "The target window is not responding.", new { step = "resolve-window" });
            return 1;
        }

        var scope = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        if (scope is null)
        {
            JsonOutput.WriteError(
                "element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.",
                new { step = "resolve-window" });
            return 1;
        }

        var inputSelector = hasInputAutomationId
            ? new Selector { Strategy = SelectorStrategy.AutomationId, Value = inputAutomationId! }
            : new Selector { Strategy = SelectorStrategy.Name, Value = inputValue! };
        var inputElement = UiaHelper.ResolveSelector(scope, inputSelector);
        if (inputElement is null)
        {
            var inputDescription = hasInputAutomationId
                ? $"AutomationId '{inputAutomationId}'"
                : $"Name '{inputValue}'";
            JsonOutput.WriteError(
                "element-not-found", $"No input element found for {inputDescription}.",
                new { step = "resolve-input" });
            return 1;
        }

        // Click the input to focus it first: uses a plain synthetic mouse click at the element's
        // bounding-rect center rather than UiaHelper.Click, which is intentionally NOT reused here
        // — UiaHelper.Click tries InvokePattern/TogglePattern before falling back to a synthetic
        // click, and if the resolved input element happens to also expose one of those patterns
        // (plausible for some custom WPF automation peers), invoking/toggling it as a side effect
        // of "just focusing before typing" would be an unintended state change distinct from what
        // Type's own click-to-focus fallback does (which is always a plain physical click, never a
        // pattern invoke on the same element being typed into).
        var inputRect = inputElement.Current.BoundingRectangle;
        NativeMethods.Click(
            (int)(inputRect.X + inputRect.Width / 2), (int)(inputRect.Y + inputRect.Height / 2));

        var method = UiaHelper.Type(inputElement, text);

        // Read-back verification is attempted whenever possible (unlike type --verify, which is
        // opt-in): this composite verb exists specifically to catch silent typing failures before
        // committing to clicking Send, so verification is not optional here. Same
        // pattern-vs-synthetic-keyboard caveat as type --verify applies: only meaningful when
        // ValuePattern was used, since GetText's Name fallback would otherwise produce a
        // false-positive mismatch for a control with no ValuePattern support.
        if (method == "pattern")
        {
            var actual = UiaHelper.GetText(inputElement);
            if (actual != text)
            {
                JsonOutput.WriteError(
                    "verify-mismatch", "The input element's text after typing did not match the input.",
                    new { step = "type-verify", expected = text, actual });
                return 1;
            }
        }

        if (hasSendAutomationId)
        {
            var sendElement = UiaHelper.ResolveSelector(
                scope, new Selector { Strategy = SelectorStrategy.AutomationId, Value = sendAutomationId! });
            if (sendElement is null)
            {
                JsonOutput.WriteError(
                    "element-not-found", $"No Send button element found for AutomationId '{sendAutomationId}'.",
                    new { step = "resolve-send" });
                return 1;
            }

            UiaHelper.Click(sendElement);
        }
        else
        {
            var keys = hasSubmitKeys ? submitKeys! : "{ENTER}";
            try
            {
                UiaHelper.SendKeys(inputElement, keys);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                JsonOutput.WriteError(
                    "invalid-argument", $"Invalid --submitKeys syntax '{keys}': {ex.Message}",
                    new { step = "submit" });
                return 1;
            }
        }

        JsonOutput.WriteSuccess(new { method });
        return 0;
    }

    private static (AutomationElement? element, string? errorCode, string? error) ResolveElement(
        IntPtr windowHwnd, Dictionary<string, string> opts)
    {
        var scope = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        return scope is null
            ? (null, "element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.")
            : ResolveElement(scope, opts);
    }

    private static (AutomationElement? element, string? errorCode, string? error) ResolveElement(
        AutomationElement scope, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("strategy", out var strategyText)
            || !Enum.TryParse<SelectorStrategy>(strategyText, ignoreCase: true, out var strategy))
        {
            return (null, "invalid-argument",
                "--strategy is required and must be one of: Name, AutomationId.");
        }

        if (!opts.TryGetValue("value", out var value))
        {
            return (null, "invalid-argument", "--value is required.");
        }

        var selector = new Selector { Strategy = strategy, Value = value };
        var element = UiaHelper.ResolveSelector(scope, selector);
        if (element is null)
        {
            return (null, "element-not-found",
                $"No element found for selector [{strategy}] '{value}'.");
        }

        return (element, null, null);
    }

    private static (AutomationElement? element, string? errorCode, string? error) ResolveWindow(
        Dictionary<string, string> opts)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            return (null, errorCode, error);
        }

        return ResolveDiscoveredWindow($"0x{windowHwnd.ToInt64():X}");
    }

    private static (IntPtr windowHwnd, string? errorCode, string? error) ResolveWindowHwnd(
        Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("hwnd", out var hwndText))
        {
            try
            {
                return (UiaHelper.ParseHwnd(hwndText), null, null);
            }
            catch (FormatException)
            {
                return (IntPtr.Zero, "invalid-argument", $"Invalid hwnd '{hwndText}'.");
            }
            catch (OverflowException)
            {
                return (IntPtr.Zero, "invalid-argument", $"Invalid hwnd '{hwndText}'.");
            }
        }

        var (pid, errorCode, error) = ResolveValidatedPid(opts);
        if (pid is null)
        {
            return (IntPtr.Zero, errorCode, error);
        }

        var windows = UiaHelper.ListTopLevelWindows(pid.Value);
        if (windows.Count == 0)
        {
            return (IntPtr.Zero, "stale-context", $"No top-level windows found for pid {pid.Value}.");
        }

        var foregroundWindows = windows.Where(window => window.IsForeground).ToList();
        if (foregroundWindows.Count == 1)
        {
            return (UiaHelper.ParseHwnd(foregroundWindows[0].Hwnd), null, null);
        }

        if (windows.Count == 1)
        {
            return (UiaHelper.ParseHwnd(windows[0].Hwnd), null, null);
        }

        return (IntPtr.Zero, "ambiguous-window",
            $"Multiple windows found for pid {pid.Value}; pass --hwnd to select one.");
    }

    private static (AutomationElement? element, string? errorCode, string? error) ResolveDiscoveredWindow(string hwnd)
    {
        var window = UiaHelper.FindWindowByHwnd(hwnd);
        return window is null
            ? (null, "element-not-found", $"No window found for hwnd '{hwnd}'.")
            : (window, null, null);
    }

    private static (int? pid, string? errorCode, string? error) ResolveValidatedPid(
        Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("pid", out var pidText) && int.TryParse(pidText, out var pid))
        {
            return IsProcessRunning(pid)
                ? (pid, null, null)
                : (null, "stale-context", $"Process {pid} is no longer available.");
        }

        if (opts.ContainsKey("pid"))
        {
            return (null, "invalid-argument", "--pid must be an integer.");
        }

        var context = SessionContext.Load();
        if (context is null)
        {
            return (null, "stale-context", "No pid available. Call 'attach' first or pass --pid.");
        }

        try
        {
            using var process = Process.GetProcessById(context.Pid);
            if (process.HasExited
                || !process.ProcessName.Equals(context.ProcessName, StringComparison.OrdinalIgnoreCase)
                || process.StartTime.ToUniversalTime() != context.StartedAtUtc)
            {
                return (null, "stale-context", "The persisted process is no longer available.");
            }

            return (context.Pid, null, null);
        }
        catch (ArgumentException)
        {
            return (null, "stale-context", "The persisted process is no longer available.");
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

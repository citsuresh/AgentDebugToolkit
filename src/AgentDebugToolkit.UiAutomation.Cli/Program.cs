using System.Diagnostics;
using System.Windows.Automation;
using AgentDebugToolkit.Core;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.UiAutomation.Cli;

// Top-level statements do not automatically apply [STAThread]; the process defaults to MTA
// unless explicitly marked. Clipboard access (System.Windows.Forms.Clipboard, used by --paste)
// requires STA and throws InvalidOperationException otherwise — discovered via live validation
// against the real Copilot Chat composer, not a theoretical concern. Existing UIA calls happen
// to work under MTA, but explicitly requesting STA here is correct for this process regardless
// (UIA itself works fine in STA too) and is the standard fix for this exact failure mode.
if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
{
    var exitCode = 1;
    var staThread = new Thread(() => exitCode = Run(args)) { };
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();
    return exitCode;
}

return Run(args);

int Run(string[] args)
{

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
        case "find-first":
            return Verbs.FindFirst(opts);
        case "find-all":
            return Verbs.FindAll(opts);
        case "wait-for-window-change":
            return Verbs.WaitForWindowChange(opts);
        case "wait-for-process-responding":
            return Verbs.WaitForProcessResponding(opts);
        case "delay":
            return Verbs.Delay(opts);
        case "set-context":
            return Verbs.SetContext(opts);
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

} // end Run

internal static class Verbs
{
    public static int Attach(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("process", out var processName))
        {
            JsonOutput.WriteError("invalid-argument", "--process is required.");
            return 1;
        }

        // Normalize a trailing ".exe" suffix before matching: Process.ProcessName never
        // includes it, so a caller passing e.g. "--process Fdm.exe" would otherwise silently
        // fail to match despite the documented "no .exe suffix assumed either way -- match
        // flexibly" contract.
        var normalizedProcessName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^".exe".Length]
            : processName;

        var candidates = Process.GetProcesses()
            .Where(p => p.ProcessName.Equals(normalizedProcessName, StringComparison.OrdinalIgnoreCase))
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
        try
        {
            SessionContext.Save(proc.Id, proc.ProcessName, proc.StartTime.ToUniversalTime());
        }
        catch (SessionContextWriteException ex)
        {
            JsonOutput.WriteError("session-context-write-failed", ex.Message);
            return 1;
        }

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

    /// <summary>
    /// Snapshots the target process's top-level window set, polls until it is stable (unchanged)
    /// for <paramref name="settleMs"/> (default 300) consecutive milliseconds, or <c>--timeoutMs</c>
    /// elapses. "Stable" is determined by comparing hwnd sets between polls; a window closing and a
    /// different one opening within the same tick still counts as a change worth re-settling on.
    /// </summary>
    public static int WaitForWindowChange(Dictionary<string, string> opts)
    {
        var (pid, errorCode, error) = ResolveValidatedPid(opts);
        if (pid is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (opts.TryGetValue("timeoutMs", out var timeoutText)
            && (!int.TryParse(timeoutText, out var parsedTimeoutMs) || parsedTimeoutMs < 0))
        {
            JsonOutput.WriteError("invalid-argument", "--timeoutMs must be a non-negative integer.");
            return 1;
        }

        if (!opts.TryGetValue("timeoutMs", out var timeoutRaw))
        {
            JsonOutput.WriteError("invalid-argument", "--timeoutMs is required.");
            return 1;
        }

        var timeoutMs = int.Parse(timeoutRaw);

        if (opts.TryGetValue("settleMs", out var settleText)
            && (!int.TryParse(settleText, out var parsedSettleMs) || parsedSettleMs < 0))
        {
            JsonOutput.WriteError("invalid-argument", "--settleMs must be a non-negative integer.");
            return 1;
        }

        var settleMs = opts.TryGetValue("settleMs", out var settleRaw) ? int.Parse(settleRaw) : 300;

        var windowsBefore = UiaHelper.ListTopLevelWindows(pid.Value);
        var stopwatch = Stopwatch.StartNew();
        var lastSnapshot = windowsBefore;
        var lastChangeElapsedMs = stopwatch.ElapsedMilliseconds;

        while (true)
        {
            Thread.Sleep(50);
            var current = UiaHelper.ListTopLevelWindows(pid.Value);
            var changed = !current.Select(w => w.Hwnd).OrderBy(h => h)
                .SequenceEqual(lastSnapshot.Select(w => w.Hwnd).OrderBy(h => h));

            if (changed)
            {
                lastSnapshot = current;
                lastChangeElapsedMs = stopwatch.ElapsedMilliseconds;
            }
            else if (stopwatch.ElapsedMilliseconds - lastChangeElapsedMs >= settleMs)
            {
                var beforeHwnds = windowsBefore.Select(w => w.Hwnd).ToHashSet();
                var afterHwnds = lastSnapshot.Select(w => w.Hwnd).ToHashSet();
                JsonOutput.WriteSuccess(new
                {
                    windowsBefore,
                    windowsAfter = lastSnapshot,
                    newWindows = lastSnapshot.Where(w => !beforeHwnds.Contains(w.Hwnd)).ToList(),
                    closedWindows = windowsBefore.Where(w => !afterHwnds.Contains(w.Hwnd)).ToList(),
                    elapsedMs = stopwatch.ElapsedMilliseconds
                });
                return 0;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                var beforeHwnds = windowsBefore.Select(w => w.Hwnd).ToHashSet();
                var afterHwnds = lastSnapshot.Select(w => w.Hwnd).ToHashSet();
                JsonOutput.WriteError(
                    "timeout",
                    $"Window set for pid {pid.Value} did not stabilize within {timeoutMs}ms.",
                    new
                    {
                        windowsBefore,
                        windowsAfter = lastSnapshot,
                        newWindows = lastSnapshot.Where(w => !beforeHwnds.Contains(w.Hwnd)).ToList(),
                        closedWindows = windowsBefore.Where(w => !afterHwnds.Contains(w.Hwnd)).ToList(),
                        elapsedMs = stopwatch.ElapsedMilliseconds
                    });
                return 1;
            }
        }
    }

    /// <summary>
    /// Polls <see cref="NativeMethods.IsResponding"/> against the target process's foreground
    /// window (falling back to "any window responds" only when there is no single unambiguous
    /// window to prefer), until it responds or <c>--timeoutMs</c> elapses. A process with no
    /// top-level windows at all is treated as not-responding (there is nothing to send the probe
    /// message to).
    /// </summary>
    public static int WaitForProcessResponding(Dictionary<string, string> opts)
    {
        var (pid, errorCode, error) = ResolveValidatedPid(opts);
        if (pid is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!opts.TryGetValue("timeoutMs", out var timeoutRaw)
            || !int.TryParse(timeoutRaw, out var timeoutMs) || timeoutMs < 0)
        {
            JsonOutput.WriteError("invalid-argument", "--timeoutMs is required and must be a non-negative integer.");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            // Prefer the foreground window (same preference as ResolveWindowHwnd) so this verb
            // reflects whether the process's *main*/active window is responsive rather than
            // reporting success merely because some secondary window (toolbar, splash, etc.) is
            // still pumping messages while the actual target window is hung. Falls back to "any
            // window responds" only when there is no single unambiguous window to prefer (no
            // foreground window and more than one top-level window) -- a process with exactly one
            // window is unambiguous regardless of foreground state.
            var windows = UiaHelper.ListTopLevelWindows(pid.Value);
            var foregroundWindows = windows.Where(w => w.IsForeground).ToList();
            var primaryWindow = foregroundWindows.Count == 1
                ? foregroundWindows[0]
                : windows.Count == 1 ? windows[0] : null;

            var responding = primaryWindow is not null
                ? NativeMethods.IsResponding(UiaHelper.ParseHwnd(primaryWindow.Hwnd), timeoutMs: 100)
                : windows.Any(w => NativeMethods.IsResponding(UiaHelper.ParseHwnd(w.Hwnd), timeoutMs: 100));

            if (responding)
            {
                JsonOutput.WriteSuccess(new { responding = true, elapsedMs = stopwatch.ElapsedMilliseconds });
                return 0;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                JsonOutput.WriteError(
                    "process-not-responding",
                    $"Process {pid.Value} did not respond within {timeoutMs}ms.");
                return 1;
            }

            Thread.Sleep(100);
        }
    }

    public static int Delay(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("ms", out var msText)
            || !int.TryParse(msText, out var ms) || ms < 0)
        {
            JsonOutput.WriteError("invalid-argument", "--ms is required and must be a non-negative integer.");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        Thread.Sleep(ms);
        JsonOutput.WriteSuccess(new { waitedMs = stopwatch.ElapsedMilliseconds });
        return 0;
    }

    /// <summary>
    /// Manual session-context override: persists <c>--pid</c> as the "current" process for
    /// subsequent invocations, the same way <see cref="Attach"/> does, without re-resolving by
    /// process name. Useful when the caller already knows the pid (e.g. from its own process
    /// launch) and wants to skip the by-name lookup.
    /// </summary>
    public static int SetContext(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, out var pid))
        {
            JsonOutput.WriteError("invalid-argument", "--pid is required and must be an integer.");
            return 1;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            JsonOutput.WriteError("stale-context", $"Process {pid} is no longer available.");
            return 1;
        }

        try
        {
            SessionContext.Save(process.Id, process.ProcessName, process.StartTime.ToUniversalTime());
        }
        catch (SessionContextWriteException ex)
        {
            JsonOutput.WriteError("session-context-write-failed", ex.Message);
            return 1;
        }

        JsonOutput.WriteSuccess(new { pid = process.Id });
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

        var usePaste = opts.TryGetValue("paste", out var pasteText) && pasteText != "false";

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

        // Reject embedded newlines only for the synthetic-keyboard path: a raw '\n'/'\r' passed
        // through to SendKeys.SendWait is not treated as literal text -- it can trigger unintended
        // UI navigation (e.g. moving focus to a different control), which was observed directly
        // during Phase 9 validation rather than being a theoretical concern. This rejection does
        // NOT apply to --paste (pasted text never passes through SendKeys.SendWait
        // character-by-character) or to ValuePattern-backed elements (SetValue bypasses SendKeys
        // entirely, so the underlying bug does not apply there either -- this is why the check now
        // happens after element resolution instead of unconditionally up front).
        if (!usePaste && !ElementSupportsValuePattern(element) && (text.Contains('\n') || text.Contains('\r')))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "--text must not contain embedded newline characters ('\\n'/'\\r'); these are not " +
                "treated as literal text by the underlying SendKeys mechanism and can trigger " +
                "unintended UI navigation instead. Use --paste to send text containing newlines.");
            return 1;
        }

        string method;
        bool? clipboardRestored = null;

        // --paste is a no-op when ValuePattern is available: SetValue is already instant and
        // non-interruptible, so there's nothing for clipboard paste to improve on. Report the
        // actual method used ("pattern") rather than silently ignoring the flag.
        if (usePaste && !ElementSupportsValuePattern(element))
        {
            try
            {
                (method, var restored) = UiaHelper.TypeViaPaste(element, text);
                clipboardRestored = restored;
            }
            catch (ClipboardUnavailableException ex)
            {
                JsonOutput.WriteError("clipboard-unavailable", ex.Message);
                return 1;
            }
        }
        else
        {
            method = UiaHelper.Type(element, text);
        }

        // Optional read-back verification: --verify (matching --screenshot's boolean-flag
        // convention: absent or "false" disables it, any other value enables it) re-reads the
        // element's text after typing and fails with verify-mismatch if it doesn't match what was
        // sent. This exists to catch silent typing failures (e.g. a control that ignored or
        // truncated the input) that the plain success response cannot detect on its own.
        //
        // Verification is meaningful for "pattern" (ValuePattern.SetValue, read back the same way)
        // and "clipboard-paste" (read back via GetTextForPasteVerification, which tries TextPattern
        // before falling back to Name — paste specifically targets elements without ValuePattern,
        // so a TextPattern attempt is needed for verification to reflect real content). When Type
        // fell back to plain synthetic-keyboard input ("synthetic-keyboard", i.e. no ValuePattern
        // support and no --paste), GetText falls back to the element's accessibility Name (a static
        // label), not its typed content — comparing that against the typed text would almost always
        // report a false-positive mismatch, not a genuine typing failure. Skip verification for
        // that case rather than produce an unreliable result.
        //
        // For "clipboard-paste" specifically, compare with line endings normalized: --paste is the
        // only path that allows embedded newlines through, and live validation against a real
        // RichEdit-based control (Notepad) showed the control itself normalizes '\n' to '\r'
        // on paste — a real editor behavior, not data loss. Comparing raw would report a false
        // verify-mismatch for any multi-line paste into such a control.
        if (opts.TryGetValue("verify", out var verifyText) && verifyText != "false"
            && (method == "pattern" || method == "clipboard-paste"))
        {
            var actual = method == "clipboard-paste"
                ? UiaHelper.GetTextForPasteVerification(element)
                : UiaHelper.GetText(element);
            var matches = method == "clipboard-paste"
                ? NormalizeLineEndings(actual) == NormalizeLineEndings(text)
                : actual == text;
            if (!matches)
            {
                JsonOutput.WriteError(
                    "verify-mismatch",
                    "The element's text after typing did not match the input.",
                    new { expected = text, actual });
                return 1;
            }
        
            // Phase 14: a matching read-back immediately after paste is not sufficient on its
            // own for "clipboard-paste" -- some target controls (notably JS-driven composers,
            // not native Win32 edit controls) asynchronously mutate/truncate/submit pasted
            // content containing embedded newlines shortly after the paste, after our one-shot
            // read already looked complete. See VerifyPasteStability's doc comment for the full
            // investigation. Not applicable to "pattern" (SetValue is synchronous, no such race).
            if (method == "clipboard-paste")
            {
                var instability = VerifyPasteStability(element, text, actual);
                if (instability is not null)
                {
                    JsonOutput.WriteError(
                        instability.Value.error, instability.Value.message,
                        new { expected = text, actual = instability.Value.actual });
                    return 1;
                }
            }
        }

        if (clipboardRestored is not null)
        {
            JsonOutput.WriteSuccess(new { method, clipboardRestored = clipboardRestored.Value });
        }
        else
        {
            JsonOutput.WriteSuccess(new { method });
        }
        return 0;
    }

    private static bool ElementSupportsValuePattern(AutomationElement element)
    {
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out _)
            && !(bool)element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty);
    }

    private static string? NormalizeLineEndings(string? s)
    {
        return s?.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // Additional settle delay for the second read in VerifyPasteStability, applied on top of
    // whatever delay TypeViaPaste already waited before the first read. Chosen to give an
    // async/JS-driven composer (as opposed to a native Win32 edit control) time to finish any
    // post-paste processing of its own (e.g. treating an embedded newline as a submit trigger)
    // before we take a second snapshot to compare against the first.
    private const int PasteStabilitySettleMs = 250;

    // Below this fraction of the input length, a verified clipboard-paste read-back is treated
    // as "suspiciously short" even if it superficially compares equal via a partial/prefix match
    // -- guards against a composer truncating to just the first line/paragraph while still
    // reporting some text that could otherwise pass a naive comparison.
    private const double PasteSuspiciousShrinkageThreshold = 0.5;

    /// <summary>
    /// Phase 14: investigates a real bug where a multi-paragraph --paste into a JS-driven chat
    /// composer reported verify success (matching read-back immediately after paste) but the
    /// composer itself then asynchronously truncated/submitted on the embedded newline shortly
    /// after -- a race between our one-shot verification and the target app's own post-paste
    /// processing, not a synchronous truncation the original single read-back could catch.
    ///
    /// Performs two checks beyond the original single read-back comparison:
    /// (A) Re-reads the element after an additional settle delay and compares against the first
    /// read; a mismatch (especially the text shrinking) means the target app mutated the content
    /// after our verification already looked complete, and is reported as "verify-unstable"
    /// rather than silently returning success.
    /// (B) Independent of (A), flags the first read as suspiciously short if its length is below
    /// PasteSuspiciousShrinkageThreshold of the input length -- catches a partial prefix that
    /// happens to satisfy the exact-match comparison used elsewhere (e.g. the caller only
    /// compared a normalized substring) without a full mismatch being detected.
    ///
    /// Only meaningful for method == "clipboard-paste" (the only path that allows embedded
    /// newlines and the only one susceptible to this app-side race); callers must not invoke this
    /// for "pattern" or "synthetic-keyboard".
    /// </summary>
    /// <returns>
    /// null if stable and not suspiciously short (caller proceeds with its own exact-match
    /// verification as before); otherwise an (error, message, actual) tuple the caller should
    /// report instead of success. `actual` is the most recent/relevant read-back evidence for the
    /// failure (the suspiciously-short first read for the shrinkage check, or the second,
    /// post-mutation read for the instability check) -- not necessarily the same value as the
    /// caller's own `firstRead`, so callers should use this `actual` rather than their own when
    /// building the error payload.
    /// </returns>
    private static (string error, string message, string? actual)? VerifyPasteStability(
        AutomationElement element, string text, string? firstRead)
    {
        if (text.Length > 0
            && (firstRead?.Length ?? 0) < text.Length * PasteSuspiciousShrinkageThreshold)
        {
            return (
                "verify-unstable",
                "The clipboard-paste read-back is suspiciously shorter than the input text " +
                "immediately after pasting; the target control may have already begun " +
                "truncating or submitting the content.",
                firstRead);
        }

        Thread.Sleep(PasteStabilitySettleMs);
        var secondRead = UiaHelper.GetTextForPasteVerification(element);
        if (NormalizeLineEndings(secondRead) != NormalizeLineEndings(firstRead))
        {
            return (
                "verify-unstable",
                "The element's text changed between two read-backs after pasting; the target " +
                "control likely mutated the pasted content asynchronously (e.g. treating an " +
                "embedded newline as a submit trigger) after verification appeared to succeed.",
                secondRead);
        }

        return null;
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
        opts.TryGetValue("strategy", out var strategyText);
        if (!UiaHelper.TryParseImplementedSelectorStrategy(strategyText, out var strategy, out var strategyError))
        {
            JsonOutput.WriteError("invalid-argument", $"--strategy is required and {strategyError}");
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

        var usePaste = opts.TryGetValue("paste", out var pasteText) && pasteText != "false";

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

        // Reject embedded newlines only for the synthetic-keyboard path -- same guard as the
        // standalone type verb (this composite verb calls UiaHelper.Type directly rather than
        // Verbs.Type, so the check is duplicated here rather than inherited; see that verb's
        // matching comment for the full rationale). Does not apply to --paste or to
        // ValuePattern-backed elements (SetValue bypasses SendKeys entirely).
        if (!usePaste && !ElementSupportsValuePattern(inputElement) && (text.Contains('\n') || text.Contains('\r')))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "--text must not contain embedded newline characters ('\\n'/'\\r'); these are not " +
                "treated as literal text by the underlying SendKeys mechanism and can trigger " +
                "unintended UI navigation instead. Use --paste to send text containing newlines.",
                new { step = "validate-arguments" });
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

        string method;
        bool? clipboardRestored = null;
        if (usePaste && !ElementSupportsValuePattern(inputElement))
        {
            try
            {
                (method, var restored) = UiaHelper.TypeViaPaste(inputElement, text);
                clipboardRestored = restored;
            }
            catch (ClipboardUnavailableException ex)
            {
                JsonOutput.WriteError("clipboard-unavailable", ex.Message, new { step = "type" });
                return 1;
            }
        }
        else
        {
            method = UiaHelper.Type(inputElement, text);
        }

        // Read-back verification is attempted whenever possible (unlike type --verify, which is
        // opt-in): this composite verb exists specifically to catch silent typing failures before
        // committing to clicking Send, so verification is not optional here. Verification applies
        // to "pattern" and "clipboard-paste" (see Verbs.Type's matching comment for why paste needs
        // GetTextForPasteVerification rather than GetText) but not plain "synthetic-keyboard",
        // where GetText's Name fallback would produce a false-positive mismatch. Line-ending
        // normalization for "clipboard-paste" mirrors Verbs.Type — see that comment for the
        // live-validated rationale (RichEdit-based controls normalize '\n' to '\r' on paste).
        if (method == "pattern" || method == "clipboard-paste")
        {
            var actual = method == "clipboard-paste"
                ? UiaHelper.GetTextForPasteVerification(inputElement)
                : UiaHelper.GetText(inputElement);
            var matches = method == "clipboard-paste"
                ? NormalizeLineEndings(actual) == NormalizeLineEndings(text)
                : actual == text;
            if (!matches)
            {
                JsonOutput.WriteError(
                    "verify-mismatch", "The input element's text after typing did not match the input.",
                    new { step = "type-verify", expected = text, actual });
                return 1;
            }
        
            // Phase 14: guard against the target composer asynchronously mutating/truncating/
            // submitting pasted content (embedded newlines) after our one-shot read looked
            // complete -- see VerifyPasteStability's doc comment and Verbs.Type's matching
            // block for the full investigation. This is especially important here because a
            // Send click follows immediately after this block; blocking on instability here
            // prevents an incomplete/mutated message from being sent.
            if (method == "clipboard-paste")
            {
                var instability = VerifyPasteStability(inputElement, text, actual);
                if (instability is not null)
                {
                    JsonOutput.WriteError(
                        instability.Value.error, instability.Value.message,
                        new { step = "type-verify", expected = text, actual = instability.Value.actual });
                    return 1;
                }
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

        if (clipboardRestored is not null)
        {
            JsonOutput.WriteSuccess(new { method, clipboardRestored = clipboardRestored.Value });
        }
        else
        {
            JsonOutput.WriteSuccess(new { method });
        }
        return 0;
    }

    /// <summary>
    /// Generic, shallow-cost single-match lookup: resolves an optional scope element (defaults
    /// to the window root) and runs a single <c>FindFirst</c> against it — no fixed-depth tree
    /// walk/serialization the way <c>inspect --maxDepth N</c> does. This is a general-purpose UIA
    /// primitive, not tied to any specific application's UI shape; callers compose whatever
    /// app-specific detection logic they need (e.g. "is a prompt pending") out of one or more
    /// calls to this and <see cref="FindAll"/>, keeping app-specific patterns in caller-side
    /// scripts/config rather than baked into this exe.
    /// </summary>
    /// <remarks>
    /// Supersedes the former <c>has-pending-prompt</c> verb, which hardcoded Copilot-Chat-specific
    /// anchor/label/option patterns (Name=="Waiting...", AutomationId=="RadioFieldLabel",
    /// ControlType.RadioButton) directly into the CLI. Those patterns still work — a caller can
    /// reproduce the same check by calling <c>find-first --strategy Name --value "Waiting..."</c>
    /// followed by <c>find-all --strategy AutomationId --value RadioFieldLabel</c> — but the
    /// values now live in the caller, not this executable. See docs/CLI_CONTRACT.md for the
    /// worked example.
    /// </remarks>
    public static int FindFirst(Dictionary<string, string> opts)
    {
        var (scope, scopeErrorCode, scopeError) = ResolveFindScope(opts);
        if (scope is null)
        {
            JsonOutput.WriteError(scopeErrorCode!, scopeError!);
            return 1;
        }

        var (strategy, value, argErrorCode, argError) = ParseSelectorArgs(opts);
        if (argErrorCode is not null)
        {
            JsonOutput.WriteError(argErrorCode, argError!);
            return 1;
        }

        var match = UiaHelper.ResolveSelector(scope, new Selector { Strategy = strategy, Value = value! });
        if (match is null)
        {
            JsonOutput.WriteSuccess(new { found = false });
            return 0;
        }

        JsonOutput.WriteSuccess(new { found = true, element = ToElementSummary(match) });
        return 0;
    }

    /// <summary>
    /// Generic, shallow-cost multi-match lookup — same scoping/strategy support as
    /// <see cref="FindFirst"/>, but returns every descendant match via <c>FindAll</c> instead of
    /// stopping at the first. <c>--excludeValue</c> is a generic convenience (skip elements whose
    /// <c>Name</c> equals this) for the common case of filtering out one known placeholder value;
    /// it is not tied to any specific application.
    /// </summary>
    public static int FindAll(Dictionary<string, string> opts)
    {
        var (scope, scopeErrorCode, scopeError) = ResolveFindScope(opts);
        if (scope is null)
        {
            JsonOutput.WriteError(scopeErrorCode!, scopeError!);
            return 1;
        }

        var (strategy, value, argErrorCode, argError) = ParseSelectorArgs(opts);
        if (argErrorCode is not null)
        {
            JsonOutput.WriteError(argErrorCode, argError!);
            return 1;
        }

        opts.TryGetValue("excludeValue", out var excludeValue);

        var matches = UiaHelper.ResolveSelectorAll(scope, new Selector { Strategy = strategy, Value = value! });
        var elements = new List<object>();
        foreach (AutomationElement match in matches)
        {
            if (excludeValue is not null && match.Current.Name == excludeValue)
            {
                continue;
            }

            elements.Add(ToElementSummary(match));
        }

        JsonOutput.WriteSuccess(new { count = elements.Count, elements });
        return 0;
    }

    private static object ToElementSummary(AutomationElement element)
    {
        return new
        {
            name = element.Current.Name ?? string.Empty,
            automationId = element.Current.AutomationId ?? string.Empty,
            controlType = element.Current.ControlType?.ProgrammaticName ?? string.Empty,
            className = element.Current.ClassName ?? string.Empty,
        };
    }

    private static (SelectorStrategy strategy, string? value, string? errorCode, string? error) ParseSelectorArgs(
        Dictionary<string, string> opts)
    {
        opts.TryGetValue("strategy", out var strategyText);
        if (!UiaHelper.TryParseImplementedSelectorStrategy(strategyText, out var strategy, out var strategyError))
        {
            return (default, null, "invalid-argument", $"--strategy is required and {strategyError}");
        }

        if (!opts.TryGetValue("value", out var value))
        {
            return (default, null, "invalid-argument", "--value is required.");
        }

        return (strategy, value, null, null);
    }

    /// <summary>
    /// Resolves the search scope for <see cref="FindFirst"/>/<see cref="FindAll"/>: the window
    /// root by default, or a descendant of it when <c>--scopeStrategy</c>/<c>--scopeValue</c> are
    /// both supplied (narrowing the search, e.g. to a specific panel, for both speed and to avoid
    /// ambiguous matches elsewhere in the window).
    /// </summary>
    private static (AutomationElement? scope, string? errorCode, string? error) ResolveFindScope(
        Dictionary<string, string> opts)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            return (null, errorCode, error);
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            return (null, "window-not-responding", "The target window is not responding.");
        }

        var window = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        if (window is null)
        {
            return (null, "element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.");
        }

        var hasScopeStrategy = opts.TryGetValue("scopeStrategy", out var scopeStrategyText);
        var hasScopeValue = opts.TryGetValue("scopeValue", out var scopeValue);
        if (!hasScopeStrategy && !hasScopeValue)
        {
            return (window, null, null);
        }

        if (!hasScopeStrategy || !hasScopeValue)
        {
            return (null, "invalid-argument", "--scopeStrategy and --scopeValue must both be supplied, or neither.");
        }

        if (!UiaHelper.TryParseImplementedSelectorStrategy(scopeStrategyText, out var scopeStrategy, out var scopeStrategyError))
        {
            return (null, "invalid-argument", $"--scopeStrategy {scopeStrategyError}");
        }

        var scopeElement = UiaHelper.ResolveSelector(window, new Selector { Strategy = scopeStrategy, Value = scopeValue! });
        if (scopeElement is null)
        {
            return (null, "element-not-found", $"No scope element found for --scopeStrategy '{scopeStrategyText}' --scopeValue '{scopeValue}'.");
        }

        return (scopeElement, null, null);
    }

    private static (AutomationElement? element, string? errorCode, string? error) ResolveElement(
        IntPtr windowHwnd, Dictionary<string, string> opts)
    {
        // --scopeHwnd, when given, narrows the search to that element (and its subtree) instead
        // of the primary --hwnd/session-context window -- e.g. a specific pane/panel handle
        // obtained from a prior inspect/find-first call, for speed and to avoid ambiguous
        // matches elsewhere in the window. Falls back to --hwnd (windowHwnd) when absent, per
        // the documented "scoped to --scopeHwnd if given, else --hwnd" contract.
        if (opts.TryGetValue("scopeHwnd", out var scopeHwndText))
        {
            IntPtr scopeHwnd;
            try
            {
                scopeHwnd = UiaHelper.ParseHwnd(scopeHwndText);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                return (null, "invalid-argument", $"Invalid scopeHwnd '{scopeHwndText}'.");
            }

            var scopeElement = UiaHelper.FindWindowByHwnd($"0x{scopeHwnd.ToInt64():X}");
            return scopeElement is null
                ? (null, "element-not-found", $"No scope element found for scopeHwnd '0x{scopeHwnd.ToInt64():X}'.")
                : ResolveElement(scopeElement, opts);
        }

        var scope = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        return scope is null
            ? (null, "element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.")
            : ResolveElement(scope, opts);
    }

    private static (AutomationElement? element, string? errorCode, string? error) ResolveElement(
        AutomationElement scope, Dictionary<string, string> opts)
    {
        opts.TryGetValue("strategy", out var strategyText);
        if (!UiaHelper.TryParseImplementedSelectorStrategy(strategyText, out var strategy, out var strategyError))
        {
            return (null, "invalid-argument",
                $"--strategy is required and {strategyError}");
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

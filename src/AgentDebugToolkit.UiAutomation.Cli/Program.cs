using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Automation;
using AgentDebugToolkit.Core;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.Inspection;
using AgentDebugToolkit.UiAutomation.Cli;

if (AgentDebugToolkit.Identity.ToolkitIdentity.TryHandle(args, out var identityExitCode)) return identityExitCode;

// Declare per-monitor DPI awareness before any window/coordinate work happens: without this,
// Windows silently virtualizes coordinates for a DPI-unaware process against a scaled ("low-res")
// virtual desktop, so SetCursorPos/mouse_event/SendInput screen coordinates computed from a real
// UIA BoundingRectangle land in the wrong place on scaled displays. Must run before any window is
// created or any DPI-sensitive API is touched, so this happens first, ahead of the STA dispatch
// below. Falls back to the older per-process (not per-monitor) SetProcessDPIAware on systems where
// SetProcessDpiAwarenessContext/the V2 context constant isn't available (pre-Windows 10 1703).
if (!NativeMethods.TrySetPerMonitorDpiAwareness() && !NativeMethods.SetProcessDPIAware())
{
    // Both the modern per-monitor-v2 API and the legacy per-process fallback failed to declare
    // DPI awareness. This is non-fatal (the process still runs), but silently continuing would
    // reproduce the exact "coordinates land in the wrong place on scaled displays" bug this
    // feature exists to fix, with no diagnostic trail. Write to stderr (not JsonOutput -- this
    // isn't a verb result, and no verb has been dispatched yet) so it's visible without breaking
    // stdout's JSON-only contract for callers that parse it.
    Console.Error.WriteLine(
        "Warning: failed to declare DPI awareness (both SetProcessDpiAwarenessContext and " +
        "SetProcessDPIAware failed); screen coordinates may be inaccurate on scaled displays.");
}

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
if (!TryParseOptions(args.Skip(1).ToArray(), out var opts, out var parseError, verb is "read-rows" or "read-tree-items" or "select-item" or "select-row"))
{
    var message = verb.Equals("list-windows", StringComparison.OrdinalIgnoreCase)
        ? $"{parseError} Accepted list-windows flags: {Verbs.ListWindowsAcceptedFlags}."
        : parseError!;
    JsonOutput.WriteError("invalid-argument", message);
    return 1;
}

if (verb is not ("inspect" or "read-visible-text" or "read-rows" or "read-tree-items" or "select-item" or "select-row") && (opts.ContainsKey("maxNodes") || opts.ContainsKey("maxDurationMs")))
{
    JsonOutput.WriteError("invalid-argument", "--maxNodes and --maxDurationMs apply only to inspection, paged readers and verified selection.");
    return 1;
}

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
        case "drag":
            return Verbs.Drag(opts);
        case "move-mouse":
            return Verbs.MoveMouse(opts);
        case "get-cursor-pos":
            return Verbs.GetCursorPos(opts);
        case "cursor-session":
            return Verbs.CursorSessionVerb(args.Skip(1).ToArray());
        case "right-click":
            return Verbs.RightClick(opts);
        case "double-click":
            return Verbs.DoubleClick(opts);
        case "type":
            return Verbs.Type(opts);
        case "set-grid-cell":
            return Verbs.SetGridCell(opts);
        case "get-text":
            return Verbs.GetText(opts);
        case "get-state":
            return Verbs.GetState(opts);
        case "set-toggle-state":
            return Verbs.SetToggleState(opts);
        case "wait-for-element":
            return Verbs.WaitForElement(opts);
        case "read-visible-text":
            return Verbs.ReadVisibleText(opts);
        case "read-rows":
            return Verbs.ReadPage(opts, tree: false);
        case "read-tree-items":
            return Verbs.ReadPage(opts, tree: true);
        case "select-item":
            return Verbs.Select(opts, row: false);
        case "select-row":
            return Verbs.Select(opts, row: true);
        case "screenshot":
            return Verbs.Screenshot(opts);
        case "activate":
            return Verbs.Activate(opts);
        case "send-keys":
            return Verbs.SendKeys(opts);
        case "paste-text":
            return Verbs.PasteText(opts);
        case "scroll":
            return Verbs.Scroll(opts);
        case "submit-chat-message":
            return Verbs.SubmitChatMessage(opts);
        case "find-first":
            return Verbs.FindFirst(opts);
        case "find-all":
            return Verbs.FindAll(opts);
        case "scroll-into-view":
            return Verbs.ScrollIntoView(opts);
        case "scroll-container":
            return Verbs.ScrollContainer(opts);
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
catch (WindowGoneException ex)
{
    JsonOutput.WriteError("window-gone", ex.Message);
    return 1;
}
catch (ForegroundActivationException ex)
{
    Verbs.WriteForegroundFailure(ex.TargetHwnd, ex.Message);
    return 1;
}
catch (InputInjectionException ex)
{
    JsonOutput.WriteError("input-injection-failed", ex.Message);
    return 1;
}
catch (Exception ex)
{
    JsonOutput.WriteError("unhandled-exception", ex.Message);
    return 1;
}

static bool TryParseOptions(string[] args, out Dictionary<string, string> dict, out string? error, bool strict = false)
{
    dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    error = null;

    for (var i = 0; i < args.Length; i++)
    {
        var argument = args[i];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            if (strict) { error = $"Unexpected positional argument '{argument}'."; return false; }
            continue;
        }

        var equalsIndex = argument.IndexOf('=');
        string key;
        string value;
        if (equalsIndex >= 0)
        {
            key = argument[2..equalsIndex];
            value = argument[(equalsIndex + 1)..];
        }
        else
        {
            key = argument[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            else if (IsBooleanFlag(key))
            {
                value = "true";
            }
            else
            {
                var clearHint = key.Equals("text", StringComparison.OrdinalIgnoreCase)
                    ? " or, for type, --clear"
                    : string.Empty;
                error = $"--{key} requires a value. Some shells (Windows PowerShell 5.1) drop empty-string arguments; use --{key}={clearHint}.";
                return false;
            }
        }

        if ((strict || key.Equals("maxNodes", StringComparison.OrdinalIgnoreCase) || key.Equals("maxDurationMs", StringComparison.OrdinalIgnoreCase)) && dict.ContainsKey(key))
        {
            error = $"--{key} must not be specified more than once.";
            return false;
        }
        dict[key] = value;
    }

    return true;
}

static bool IsBooleanFlag(string name) => name.Equals("screenshot", StringComparison.OrdinalIgnoreCase)
    || name.Equals("paste", StringComparison.OrdinalIgnoreCase)
    || name.Equals("verify", StringComparison.OrdinalIgnoreCase)
    || name.Equals("clear", StringComparison.OrdinalIgnoreCase)
    || name.Equals("all", StringComparison.OrdinalIgnoreCase)
    || name.Equals("includeUntitled", StringComparison.OrdinalIgnoreCase)
    || name.Equals("allowOutsideWindow", StringComparison.OrdinalIgnoreCase);

} // end Run

internal static class Verbs
{
    internal const string ListWindowsAcceptedFlags =
        "--pid, --all, --processName, --titleContains, --className, --limit, --includeUntitled";

    private static readonly HashSet<string> ListWindowsOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pid", "all", "processName", "titleContains", "className", "limit", "includeUntitled"
    };

    internal static bool TryParseCursorSessionArguments(string[] arguments, out bool start,
        out CursorSessionKind? kind, out string? error)
    {
        start = arguments.Length > 0 && arguments[0] == "start";
        kind = null;
        error = null;
        if (arguments.Length == 0 || arguments[0] is not ("start" or "stop"))
        {
            error = "Use cursor-session start|stop [--kind UiAutomation|Orchestrator].";
            return false;
        }

        string? value = null;
        if (arguments.Length == 2 && arguments[1].StartsWith("--kind=", StringComparison.OrdinalIgnoreCase))
        {
            value = arguments[1]["--kind=".Length..];
        }
        else if (arguments.Length == 3 && arguments[1].Equals("--kind", StringComparison.OrdinalIgnoreCase))
        {
            value = arguments[2];
        }
        else if (arguments.Length != 1)
        {
            error = "Use cursor-session start|stop [--kind UiAutomation|Orchestrator].";
            return false;
        }

        if (value is not null)
        {
            if (value.Equals(nameof(CursorSessionKind.UiAutomation), StringComparison.OrdinalIgnoreCase))
            {
                kind = CursorSessionKind.UiAutomation;
            }
            else if (value.Equals(nameof(CursorSessionKind.Orchestrator), StringComparison.OrdinalIgnoreCase))
            {
                kind = CursorSessionKind.Orchestrator;
            }
            else
            {
                error = "--kind must be UiAutomation or Orchestrator.";
                return false;
            }
        }
        return true;
    }

    public static int CursorSessionVerb(string[] arguments)
    {
        if (!TryParseCursorSessionArguments(arguments, out var start, out var kind, out var error))
        {
            JsonOutput.WriteError("invalid-argument", error!);
            return 1;
        }

        var session = new CursorSession(new SystemCursorOperations(),
            CursorSession.FlagPath(Environment.GetEnvironmentVariable("TEMP"), Path.GetTempPath()));
        var result = start
            ? session.Start(kind ?? CursorSessionKind.UiAutomation)
            : session.Stop(kind);
        var payload = new Dictionary<string, object?>
        {
            ["kind"] = result.Kind,
            ["owner"] = result.Owner,
            ["changed"] = result.Changed
        };
        if (result.Warning is not null)
        {
            payload["warning"] = result.Warning;
        }
        JsonOutput.WriteSuccess(payload, preserveNullFields: true);
        return 0;
    }

    public static int Attach(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("process", out var processName))
        {
            JsonOutput.WriteError("invalid-argument", "--process is required.");
            return 1;
        }

        // Normalize a trailing ".exe" suffix before matching: Process.ProcessName never
        // includes it, so a caller passing e.g. "--process TargetApp.exe" would otherwise silently
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
        var unknownOption = opts.Keys.FirstOrDefault(key => !ListWindowsOptionNames.Contains(key));
        if (unknownOption is not null)
        {
            JsonOutput.WriteError("invalid-argument",
                $"Unknown flag '--{unknownOption}' for list-windows. Accepted flags: {ListWindowsAcceptedFlags}.");
            return 1;
        }

        var (query, errorCode, error) = ResolveWindowQuery(opts);
        if (query is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!TryParseWindowLimit(opts, out var limit, out error))
        {
            JsonOutput.WriteError("invalid-argument", error!);
            return 1;
        }

        var windows = UiaHelper.QueryTopLevelWindows(query, new Dictionary<int, string?>());
        var returnedWindows = windows.Take(limit).ToList();
        JsonOutput.WriteSuccess(new
        {
            windows = returnedWindows,
            count = returnedWindows.Count,
            truncated = windows.Count > limit
        });
        return 0;
    }

    /// <summary>
    /// Polls the filtered visible top-level window set until it settles or, with --until match,
    /// until at least one matching window remains stable for settleMs.
    /// </summary>
    public static int WaitForWindowChange(Dictionary<string, string> opts)
    {
        var (query, errorCode, error) = ResolveWindowQuery(opts);
        if (query is null)
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
        var until = opts.TryGetValue("until", out var untilValue) ? untilValue : "settle";
        if (!until.Equals("settle", StringComparison.OrdinalIgnoreCase)
            && !until.Equals("match", StringComparison.OrdinalIgnoreCase))
        {
            JsonOutput.WriteError("invalid-argument", "--until must be 'settle' or 'match'.");
            return 1;
        }

        var untilMatch = until.Equals("match", StringComparison.OrdinalIgnoreCase);
        if (untilMatch && query.TitleContains is null && query.ClassName is null && query.ProcessName is null)
        {
            JsonOutput.WriteError("invalid-argument", "--until match requires --titleContains, --className, or --processName.");
            return 1;
        }

        if (!TryParseWindowLimit(opts, out var limit, out error))
        {
            JsonOutput.WriteError("invalid-argument", error!);
            return 1;
        }

        if (!TryParseExcludedHwnds(opts, out var excludedHwnds, out error))
        {
            JsonOutput.WriteError("invalid-argument", error!);
            return 1;
        }

        var processNameCache = new Dictionary<int, string?>();
        List<WindowInfo> Snapshot() => UiaHelper.QueryTopLevelWindows(query, processNameCache)
            .Where(window => !excludedHwnds.Contains(ParseWindowHandle(window.Hwnd)))
            .ToList();

        var windowsBefore = Snapshot();
        var stopwatch = Stopwatch.StartNew();
        var lastSnapshot = windowsBefore;
        var lastChangeElapsedMs = stopwatch.ElapsedMilliseconds;
        long? matchingSinceElapsedMs = lastSnapshot.Count > 0 ? 0 : null;

        while (true)
        {
            Thread.Sleep(50);
            var current = Snapshot();
            var changedSinceLast = !SameWindowSet(current, lastSnapshot);
            var elapsedMs = stopwatch.ElapsedMilliseconds;

            if (changedSinceLast)
            {
                lastChangeElapsedMs = elapsedMs;
            }

            if (untilMatch)
            {
                if (current.Count == 0)
                {
                    matchingSinceElapsedMs = null;
                }
                else if (changedSinceLast || matchingSinceElapsedMs is null)
                {
                    matchingSinceElapsedMs = elapsedMs;
                }
            }

            lastSnapshot = current;
            var isStable = untilMatch
                ? matchingSinceElapsedMs is long matchedSince && elapsedMs - matchedSince >= settleMs
                : elapsedMs - lastChangeElapsedMs >= settleMs;

            if (isStable)
            {
                JsonOutput.WriteSuccess(CreateWindowChangePayload(
                    windowsBefore, lastSnapshot, elapsedMs, query.IsCrossProcess, limit, untilMatch, timedOut: false));
                return 0;
            }

            if (elapsedMs >= timeoutMs)
            {
                JsonOutput.WriteError(
                    "timeout",
                    $"Window set did not satisfy the requested condition within {timeoutMs}ms.",
                    CreateWindowChangePayload(
                        windowsBefore, lastSnapshot, elapsedMs, query.IsCrossProcess, limit, untilMatch, timedOut: true));
                return 1;
            }
        }
    }

    private static object CreateWindowChangePayload(
        List<WindowInfo> windowsBefore,
        List<WindowInfo> windowsAfter,
        long elapsedMs,
        bool isCrossProcess,
        int limit,
        bool includeMatchedWindows,
        bool timedOut)
    {
        var beforeHwnds = windowsBefore.Select(window => window.Hwnd).ToHashSet();
        var afterHwnds = windowsAfter.Select(window => window.Hwnd).ToHashSet();
        var newWindows = windowsAfter.Where(window => !beforeHwnds.Contains(window.Hwnd)).ToList();
        var closedWindows = windowsBefore.Where(window => !afterHwnds.Contains(window.Hwnd)).ToList();
        var truncated = false;
        var returnedBefore = LimitWindows(windowsBefore, isCrossProcess, limit, ref truncated);
        var returnedAfter = LimitWindows(windowsAfter, isCrossProcess, limit, ref truncated);
        var returnedNew = LimitWindows(newWindows, isCrossProcess, limit, ref truncated);
        var returnedClosed = LimitWindows(closedWindows, isCrossProcess, limit, ref truncated);
        var changed = !beforeHwnds.SetEquals(afterHwnds);

        if (includeMatchedWindows)
        {
            var matchedWindows = timedOut
                ? new List<WindowInfo>()
                : LimitWindows(windowsAfter, isCrossProcess, limit, ref truncated);
            return new
            {
                windowsBefore = returnedBefore,
                windowsAfter = returnedAfter,
                newWindows = returnedNew,
                closedWindows = returnedClosed,
                matchedWindows,
                elapsedMs,
                changed,
                truncated
            };
        }

        return new
        {
            windowsBefore = returnedBefore,
            windowsAfter = returnedAfter,
            newWindows = returnedNew,
            closedWindows = returnedClosed,
            elapsedMs,
            changed,
            truncated
        };
    }

    private static List<WindowInfo> LimitWindows(
        List<WindowInfo> windows,
        bool isCrossProcess,
        int limit,
        ref bool truncated)
    {
        if (isCrossProcess && windows.Count > limit)
        {
            truncated = true;
            return windows.Take(limit).ToList();
        }

        return windows;
    }

    private static bool SameWindowSet(List<WindowInfo> first, List<WindowInfo> second)
    {
        return first.Select(window => window.Hwnd).ToHashSet()
            .SetEquals(second.Select(window => window.Hwnd));
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
        if (!InspectionLimits.TryParse(opts, out var limits, out var budgetError))
        {
            JsonOutput.WriteError("invalid-argument", budgetError!);
            return 1;
        }
        opts.TryGetValue("nameContains", out var nameContains);
        ControlType? controlType = null;
        if (opts.TryGetValue("controlType", out var controlTypeText)
            && !UiaHelper.TryParseControlType(controlTypeText, out controlType))
        {
            JsonOutput.WriteError("invalid-argument", "--controlType must be a recognized short UIA control type name, such as Button or Edit.");
            return 1;
        }

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
        var takeScreenshot = opts.TryGetValue("screenshot", out var ssText)
            && ssText.Equals("true", StringComparison.OrdinalIgnoreCase);

        var filtered = nameContains is not null || controlType is not null;
        var depthStats = new DepthLimitStats();
        object? traversalReport = null;
        bool? truncated = null;
        ElementInfo? tree;
        int matchCount;
        if (limits.IsBounded)
        {
            var traversal = new InspectionTraversal(limits);
            tree = UiaHelper.InspectBounded(element, maxDepth, traversal, depthStats, nameContains, controlType, out matchCount);
            traversalReport = traversal.Report;
            truncated = traversal.Truncated;
            if (tree is null)
            {
                return WriteIncompleteInspection(traversalReport);
            }
        }
        else tree = UiaHelper.ToElementInfo(element, includeChildren: true, maxDepth: maxDepth,
            nameContains: nameContains, controlType: controlType, out matchCount, depthStats, includeState: true);
        string? screenshotPath = takeScreenshot ? ScreenshotHelper.Capture(element) : null;

        // Depth-limit fields are omitted (null) unless at least one node at the limit has children.
        // Null properties are not serialized, so existing responses are unchanged otherwise.
        var limited = depthStats.IsLimited;
        JsonOutput.WriteSuccess(new
        {
            root = tree,
            traversal = traversalReport,
            truncated,
            screenshotPath,
            matchCount = filtered ? matchCount : (int?)null,
            depthLimited = limited ? true : (bool?)null,
            maxDepth = limited ? maxDepth : (int?)null,
            nodesBeyondDepth = limited ? depthStats.NodesBeyondDepth : (int?)null,
            nodesBeyondDepthCapped = limited && depthStats.Capped ? true : (bool?)null,
            hint = limited ? depthStats.Hint(maxDepth) : null,
            lookupDiagnostics = filtered && matchCount == 0 ? CreateLookupDiagnostics(windowHwnd) : null,
        });
        return 0;
    }

    public static int Click(Dictionary<string, string> opts)
    {
        if (!TryParseActionCoordinates(opts, out var x, out var y, out var allowOutsideWindow, out var targetError))
        {
            JsonOutput.WriteError("invalid-argument", targetError!);
            return 1;
        }

        var hasCoordinates = opts.ContainsKey("x") || opts.ContainsKey("y");
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        if (hasCoordinates)
        {
            if (!TryValidateCoordinateTarget(windowHwnd, x, y, allowOutsideWindow, out var preflightError, skipIfMinimized: true))
            {
                JsonOutput.WriteError("invalid-argument", preflightError!);
                return 1;
            }

            if (!EnsureTargetWindowForeground(windowHwnd, restoreIfMinimized: true))
            {
                WriteForegroundFailure(windowHwnd);
                return 1;
            }

            if (!TryValidateCoordinateTarget(windowHwnd, x, y, allowOutsideWindow, out var boundsError))
            {
                JsonOutput.WriteError("invalid-argument", boundsError!);
                return 1;
            }

            NativeMethods.Click(x, y);
            JsonOutput.WriteSuccess(new { method = "synthetic-click", x, y });
            return 0;
        }

        var (element, elementErrorCode, elementError, elementErrorDetails) =
            ResolveElement(windowHwnd, opts, includeControlType: true);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
            return 1;
        }

        if (!EnsureElementForeground(element, windowHwnd, out var activationHwnd))
        {
            WriteForegroundFailure(activationHwnd);
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

    public static int RightClick(Dictionary<string, string> opts)
    {
        if (!TryParseActionCoordinates(opts, out var x, out var y, out var allowOutsideWindow, out var targetError))
        {
            JsonOutput.WriteError("invalid-argument", targetError!);
            return 1;
        }

        var hasCoordinates = opts.ContainsKey("x") || opts.ContainsKey("y");
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        if (hasCoordinates)
        {
            if (!TryValidateCoordinateTarget(windowHwnd, x, y, allowOutsideWindow, out var preflightError, skipIfMinimized: true))
            {
                JsonOutput.WriteError("invalid-argument", preflightError!);
                return 1;
            }

            if (!EnsureTargetWindowForeground(windowHwnd, restoreIfMinimized: true))
            {
                WriteForegroundFailure(windowHwnd);
                return 1;
            }

            if (!TryValidateCoordinateTarget(windowHwnd, x, y, allowOutsideWindow, out var boundsError))
            {
                JsonOutput.WriteError("invalid-argument", boundsError!);
                return 1;
            }

            NativeMethods.RightClick(x, y);
            JsonOutput.WriteSuccess(new { method = "synthetic-right-click", x, y });
            return 0;
        }

        var (element, elementErrorCode, elementError, elementErrorDetails) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
            return 1;
        }

        if (!EnsureElementForeground(element, windowHwnd, out var activationHwnd))
        {
            WriteForegroundFailure(activationHwnd);
            return 1;
        }

        // Same pre-action-state capture rule as Click/Drag.
        var info = UiaHelper.ToElementInfo(element, includeChildren: false, maxDepth: 0);
        var method = UiaHelper.RightClick(element);
        JsonOutput.WriteSuccess(new { method, elementFound = info });
        return 0;
    }

    public static int DoubleClick(Dictionary<string, string> opts)
    {
        if (!TryParseActionCoordinates(opts, out var x, out var y, out var allowOutsideWindow, out var targetError))
        {
            JsonOutput.WriteError("invalid-argument", targetError!);
            return 1;
        }

        var hasCoordinates = opts.ContainsKey("x") || opts.ContainsKey("y");
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        if (hasCoordinates)
        {
            if (!TryValidateCoordinateTarget(windowHwnd, x, y, allowOutsideWindow, out var preflightError, skipIfMinimized: true))
            {
                JsonOutput.WriteError("invalid-argument", preflightError!);
                return 1;
            }

            if (!EnsureTargetWindowForeground(windowHwnd, restoreIfMinimized: true))
            {
                WriteForegroundFailure(windowHwnd);
                return 1;
            }

            if (!TryValidateCoordinateTarget(windowHwnd, x, y, allowOutsideWindow, out var boundsError))
            {
                JsonOutput.WriteError("invalid-argument", boundsError!);
                return 1;
            }

            NativeMethods.DoubleClick(x, y);
            JsonOutput.WriteSuccess(new { method = "synthetic-double-click", x, y });
            return 0;
        }

        var (element, elementErrorCode, elementError, elementErrorDetails) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
            return 1;
        }

        if (!EnsureElementForeground(element, windowHwnd, out var activationHwnd))
        {
            WriteForegroundFailure(activationHwnd);
            return 1;
        }

        // Same pre-action-state capture rule as Click/Drag.
        var info = UiaHelper.ToElementInfo(element, includeChildren: false, maxDepth: 0);
        var method = UiaHelper.DoubleClick(element);
        JsonOutput.WriteSuccess(new { method, elementFound = info });
        return 0;
    }

    public static int Drag(Dictionary<string, string> opts)
    {
        var hasTargetSelector = opts.ContainsKey("targetStrategy") || opts.ContainsKey("targetValue");
        var hasTargetCoords = opts.ContainsKey("targetX") || opts.ContainsKey("targetY");

        if (hasTargetSelector == hasTargetCoords)
        {
            // Both given or neither given -- exactly one target form is required, mirroring the
            // documented mutual-exclusivity contract (same style as remove-breakpoint's
            // --all vs --file/--line check in agentdebug-vs).
            JsonOutput.WriteError("invalid-argument",
                "Exactly one of --targetStrategy/--targetValue or --targetX/--targetY is required.");
            return 1;
        }

        var steps = 15;
        if (opts.TryGetValue("steps", out var stepsText))
        {
            // Upper-bounded to prevent an unreasonably long-running/CPU-bound loop from a caller
            // passing a huge --steps value (each step is a SetCursorPos call with no cancellation
            // -- with a small/zero --durationMs this would otherwise busy-loop for a very long
            // time with no way for the caller to bound or cancel it).
            if (!int.TryParse(stepsText, out steps) || steps is <= 0 or > 1000)
            {
                JsonOutput.WriteError("invalid-argument", "--steps must be a positive integer no greater than 1000.");
                return 1;
            }
        }

        var durationMs = 300;
        if (opts.TryGetValue("durationMs", out var durationText))
        {
            if (!int.TryParse(durationText, out durationMs) || durationMs < 0)
            {
                JsonOutput.WriteError("invalid-argument", "--durationMs must be a non-negative integer.");
                return 1;
            }
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var (element, elementErrorCode, elementError, elementErrorDetails) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
            return 1;
        }

        int targetX = 0, targetY = 0;
        AutomationElement? destination = null;
        if (hasTargetSelector)
        {
            var (targetElement, targetErrorCode, targetError, targetErrorDetails) = ResolveElement(
                windowHwnd, opts, strategyKey: "targetStrategy", valueKey: "targetValue");
            if (targetElement is null)
            {
                JsonOutput.WriteError(targetErrorCode!, targetError!, targetErrorDetails);
                return 1;
            }

            destination = targetElement;
        }
        else
        {
            if (!opts.TryGetValue("targetX", out var xText) || !int.TryParse(xText, out targetX)
                || !opts.TryGetValue("targetY", out var yText) || !int.TryParse(yText, out targetY))
            {
                JsonOutput.WriteError("invalid-argument", "--targetX and --targetY must both be valid integers.");
                return 1;
            }
        }

        if (!EnsureElementForeground(element, windowHwnd, out var activationHwnd))
        {
            WriteForegroundFailure(activationHwnd);
            return 1;
        }

        if (destination is not null)
        {
            var tr = destination.Current.BoundingRectangle;
            targetX = (int)(tr.X + tr.Width / 2);
            targetY = (int)(tr.Y + tr.Height / 2);
        }

        // Capture elementFound BEFORE invoking the drag, not after -- same call-ordering rule as
        // Click: the drag can change the element's state or move it, so reading it afterward
        // would report the drag's own aftereffect rather than the pre-drag target.
        var info = UiaHelper.ToElementInfo(element, includeChildren: false, maxDepth: 0);
        var r = element.Current.BoundingRectangle;
        var sourceX = (int)(r.X + r.Width / 2);
        var sourceY = (int)(r.Y + r.Height / 2);

        NativeMethods.Drag(sourceX, sourceY, targetX, targetY, steps, durationMs);

        JsonOutput.WriteSuccess(new
        {
            method = "synthetic-drag",
            elementFound = info,
            source = new { x = sourceX, y = sourceY },
            target = new { x = targetX, y = targetY }
        });
        return 0;
    }

    /// <summary>
    /// Moves the cursor to an absolute screen position without pressing any mouse button.
    /// Supports the same selector-or-coordinates destination form as drag's target, for
    /// consistency, plus a plain --x/--y form is also accepted directly (no --strategy needed)
    /// since there's no "source" element to resolve here, unlike drag.
    /// </summary>
    public static int MoveMouse(Dictionary<string, string> opts)
    {
        var hasSelector = opts.ContainsKey("strategy") || opts.ContainsKey("value");
        var hasCoords = opts.ContainsKey("x") || opts.ContainsKey("y");

        if (hasSelector == hasCoords)
        {
            JsonOutput.WriteError("invalid-argument",
                "Exactly one of --strategy/--value or --x/--y is required.");
            return 1;
        }

        int x, y;
        if (hasSelector)
        {
            var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
            if (errorCode is not null)
            {
                JsonOutput.WriteError(errorCode, error!);
                return 1;
            }

            if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
            {
                NativeMethods.EnsureWindowExists(windowHwnd);
                JsonOutput.WriteError("window-not-responding", "Target window is not responding.");
                return 1;
            }

            var (element, elementErrorCode, elementError, elementErrorDetails) = ResolveElement(windowHwnd, opts);
            if (element is null)
            {
                JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
                return 1;
            }

            if (!EnsureElementForeground(element, windowHwnd, out var activationHwnd))
            {
                WriteForegroundFailure(activationHwnd);
                return 1;
            }

            var r = element.Current.BoundingRectangle;
            x = (int)(r.X + r.Width / 2);
            y = (int)(r.Y + r.Height / 2);
        }
        else
        {
            if (!opts.TryGetValue("x", out var xText) || !int.TryParse(xText, out x)
                || !opts.TryGetValue("y", out var yText) || !int.TryParse(yText, out y))
            {
                JsonOutput.WriteError("invalid-argument", "--x and --y must both be valid integers.");
                return 1;
            }
        }

        NativeMethods.SetCursorPos(x, y);
        JsonOutput.WriteSuccess(new { x, y });
        return 0;
    }

    public static int GetCursorPos(Dictionary<string, string> opts)
    {
        var (x, y) = NativeMethods.GetCursorPos();
        JsonOutput.WriteSuccess(new { x, y });
        return 0;
    }

    public static int Type(Dictionary<string, string> opts)
    {
        var hasText = opts.TryGetValue("text", out var text);
        var hasClearOption = opts.TryGetValue("clear", out var clearText);
        var useClear = hasClearOption && clearText != "false";
        var usePaste = opts.TryGetValue("paste", out var pasteText) && pasteText != "false";

        if (useClear && (hasText || usePaste))
        {
            JsonOutput.WriteError("invalid-argument", "--clear is mutually exclusive with --text and --paste.");
            return 1;
        }

        if (!hasText && !useClear)
        {
            JsonOutput.WriteError("invalid-argument", "One of --text or --clear is required.");
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
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var (element, elementErrorCode, elementError, elementErrorDetails) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
            return 1;
        }

        if (!EnsureElementForeground(element, windowHwnd, out var activationHwnd))
        {
            WriteForegroundFailure(activationHwnd);
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
        if (!useClear && !usePaste && !ElementSupportsValuePattern(element) && (text!.Contains('\n') || text.Contains('\r')))
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

        if (useClear)
        {
            method = UiaHelper.Clear(element);
        }
        // --paste is a no-op when ValuePattern is available: SetValue is already instant and
        // non-interruptible, so there's nothing for clipboard paste to improve on. Report the
        // actual method used ("pattern") rather than silently ignoring the flag.
        else if (usePaste && !ElementSupportsValuePattern(element))
        {
            try
            {
                (method, var restored) = UiaHelper.TypeViaPaste(element, text!);
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
            method = UiaHelper.Type(element, text!);
        }

        // Optional read-back verification: --verify (a boolean flag: absent or "false"
        // disables it, any other value enables it; unlike --screenshot, which saves only for
        // "true") re-reads the
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
            && (method == "pattern" || method == "clipboard-paste" || method == "pattern-clear"))
        {
            var expected = useClear ? string.Empty : text!;
            var actual = method == "clipboard-paste"
                ? UiaHelper.GetTextForPasteVerification(element)
                : UiaHelper.GetText(element);
            var matches = method == "clipboard-paste"
                ? NormalizeLineEndings(actual) == NormalizeLineEndings(expected)
                : actual == expected;
            if (!matches)
            {
                JsonOutput.WriteError(
                    "verify-mismatch",
                    "The element's text after the operation did not match the requested value.",
                    new { expected, actual });
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
                var instability = VerifyPasteStability(element, expected, actual);
                if (instability is not null)
                {
                    JsonOutput.WriteError(
                        instability.Value.error, instability.Value.message,
                        new { expected, actual = instability.Value.actual });
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

    /// <summary>
    /// Locates a virtualized grid row by one of its descendant values, selects a cell by UIA column
    /// index, and sets its realized editor without relying on a previously observed screen position.
    /// </summary>
    public static int SetGridCell(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("text", out var text))
        {
            JsonOutput.WriteError("invalid-argument", "--text is required.");
            return 1;
        }

        if (!TryParseNamedSelector(opts, "grid", out var gridSelector, out var gridSelectorError))
        {
            JsonOutput.WriteError("invalid-argument", gridSelectorError!);
            return 1;
        }

        if (!TryParseNamedSelector(opts, "row", out var rowSelector, out var rowSelectorError))
        {
            JsonOutput.WriteError("invalid-argument", rowSelectorError!);
            return 1;
        }

        if (!opts.TryGetValue("columnIndex", out var columnIndexText)
            || !int.TryParse(columnIndexText, out var columnIndex)
            || columnIndex < 0)
        {
            JsonOutput.WriteError("invalid-argument", "--columnIndex is required and must be a non-negative integer.");
            return 1;
        }

        var editorControlTypeText = opts.TryGetValue("editorControlType", out var suppliedEditorControlType)
            ? suppliedEditorControlType
            : "Edit";
        if (!UiaHelper.TryParseGridEditorControlType(
                editorControlTypeText, out var editorControlType, out var editorControlTypeError))
        {
            JsonOutput.WriteError("invalid-argument", $"--editorControlType {editorControlTypeError}");
            return 1;
        }

        var hasApplyStrategy = opts.ContainsKey("applyStrategy");
        var hasApplyValue = opts.ContainsKey("applyValue");
        if (hasApplyStrategy != hasApplyValue)
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "--applyStrategy and --applyValue must both be supplied, or neither.");
            return 1;
        }

        Selector? applySelector = null;
        if (hasApplyStrategy
            && !TryParseNamedSelector(opts, "apply", out applySelector, out var applySelectorError))
        {
            JsonOutput.WriteError("invalid-argument", applySelectorError!);
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
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var window = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        if (window is null)
        {
            JsonOutput.WriteError("element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.");
            return 1;
        }

        var grid = UiaHelper.ResolveSelector(window, gridSelector);
        if (grid is null)
        {
            JsonOutput.WriteError("element-not-found", "No grid found for the supplied grid selector.",
                new { lookupDiagnostics = CreateLookupDiagnostics(windowHwnd) });
            return 1;
        }

        var resolution = UiaHelper.ResolveGridCell(grid, rowSelector, columnIndex);
        if (resolution is null)
        {
            JsonOutput.WriteError(
                "element-not-found",
                $"No grid cell was found for row selector [{rowSelector.Strategy}] '{rowSelector.Value}' and column {columnIndex}.",
                new { lookupDiagnostics = CreateLookupDiagnostics(windowHwnd) });
            return 1;
        }

        if (!EnsureElementForeground(resolution.Cell, windowHwnd, out var cellActivationHwnd))
        {
            WriteForegroundFailure(cellActivationHwnd);
            return 1;
        }

        var editor = UiaHelper.ResolveGridEditor(resolution.Cell, editorControlType);
        if (editor is null)
        {
            JsonOutput.WriteError(
                "element-not-found",
                $"No {editorControlTypeText} editor was found in grid column {columnIndex}.",
                new { lookupDiagnostics = CreateLookupDiagnostics(windowHwnd) });
            return 1;
        }

        if (!EnsureElementForeground(editor, windowHwnd, out var editorActivationHwnd))
        {
            WriteForegroundFailure(editorActivationHwnd);
            return 1;
        }

        editor.SetFocus();
        var row = ToElementSummary(resolution.Row);
        var cell = ToElementSummary(resolution.Cell);
        var editorInfo = ToElementSummary(editor);
        string editorMethod;
        if (editorControlType == ControlType.CheckBox)
        {
            if (!bool.TryParse(text, out var isChecked))
            {
                JsonOutput.WriteError("invalid-argument", "--text must be true or false for a CheckBox editor.");
                return 1;
            }

            if (!UiaHelper.TrySetChecked(editor, isChecked))
            {
                JsonOutput.WriteError(
                    "element-not-found",
                    "The resolved CheckBox editor does not support TogglePattern.");
                return 1;
            }

            editorMethod = "pattern";
        }
        else
        {
            editorMethod = UiaHelper.Type(editor, text);
        }

        string? applyMethod = null;
        if (applySelector is not null)
        {
            var applyElement = UiaHelper.ResolveSelector(resolution.Cell, applySelector);
            if (applyElement is null)
            {
                JsonOutput.WriteError("element-not-found", "No apply element was found in the resolved grid cell.");
                return 1;
            }

            applyMethod = UiaHelper.Click(applyElement);
        }

        JsonOutput.WriteSuccess(
            new { row, cell, editor = editorInfo, editorMethod, applyMethod },
            preserveNullFields: true);
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

        var focusModeText = opts.TryGetValue("focusMode", out var suppliedFocusMode)
            ? suppliedFocusMode
            : "set-focus";
        if (!TryParseKeyFocusMode(focusModeText, out var focusMode))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "--focusMode must be one of: set-focus, click, none.");
            return 1;
        }

        if (!opts.ContainsKey("strategy") && !opts.ContainsKey("value") && !opts.ContainsKey("hwnd"))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "Window-level send-keys (no --strategy/--value) requires an explicit --hwnd.");
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
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        if (!opts.ContainsKey("strategy") && !opts.ContainsKey("value"))
        {
            // Window-level form: no selector, so the keys go to whatever currently has focus in
            // the --hwnd window. This is the only way to send keys to an app whose content is not
            // exposed through UI Automation (for example an Electron/Chromium window).
            if (opts.ContainsKey("scopeHwnd")
                || (opts.TryGetValue("focusMode", out var windowFocusMode)
                    && !windowFocusMode.Equals("none", StringComparison.OrdinalIgnoreCase)))
            {
                JsonOutput.WriteError(
                    "invalid-argument",
                    "Without --strategy/--value, send-keys targets the window's current focus: " +
                    "--scopeHwnd is not allowed and --focusMode, if given, must be none.");
                return 1;
            }

            if (!EnsureTargetWindowForeground(windowHwnd, restoreIfMinimized: true))
            {
                WriteForegroundFailure(windowHwnd);
                return 1;
            }

            try
            {
                NativeMethods.SendKeysRaw(keys);
                JsonOutput.WriteSuccess(new { sent = true, focusMethod = "window" });
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                JsonOutput.WriteError("invalid-argument", $"Invalid --keys syntax '{keys}': {ex.Message}");
                return 1;
            }
        }

        var (element, elementErrorCode, elementError, elementErrorDetails) = ResolveElement(windowHwnd, opts);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
            return 1;
        }

        if (!EnsureElementForeground(element, windowHwnd, out var activationHwnd))
        {
            WriteForegroundFailure(activationHwnd);
            return 1;
        }

        try
        {
            var focusMethod = UiaHelper.SendKeys(element, keys, focusMode);
            JsonOutput.WriteSuccess(new { sent = true, focusMethod });
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            // SendKeys.SendWait throws for malformed syntax (e.g. an unbalanced "{" or an
            // unrecognized key name like "{FOO}") — surface this as a clean invalid-argument
            // instead of letting it propagate to the top-level unhandled-exception handler.
            JsonOutput.WriteError("invalid-argument", $"Invalid --keys syntax '{keys}': {ex.Message}");
            return 1;
        }

    }

    /// <summary>
    /// paste-text --hwnd h --text s: puts the text on the clipboard (excluded from clipboard
    /// history and sync, prior contents restored afterwards) and sends Ctrl+V to whatever has
    /// focus in the window. No selector, no click, so it works for apps whose content is not
    /// exposed through UI Automation. Embedded newlines are allowed; that is the point.
    /// </summary>
    public static int PasteText(Dictionary<string, string> opts)
    {
        if (!opts.ContainsKey("hwnd"))
        {
            JsonOutput.WriteError("invalid-argument", "paste-text requires an explicit --hwnd.");
            return 1;
        }

        if (!opts.TryGetValue("text", out var text) || text.Length == 0)
        {
            JsonOutput.WriteError("invalid-argument", "--text is required and must be non-empty.");
            return 1;
        }

        var restoreDelayMs = UiaHelper.DefaultRestoreDelayMs;
        if (opts.TryGetValue("restoreDelayMs", out var delayText)
            && (!int.TryParse(delayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out restoreDelayMs)
                || restoreDelayMs < 0 || restoreDelayMs > 5000))
        {
            JsonOutput.WriteError("invalid-argument", "--restoreDelayMs must be an integer from 0 to 5000.");
            return 1;
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        if (!EnsureTargetWindowForeground(windowHwnd, restoreIfMinimized: true))
        {
            WriteForegroundFailure(windowHwnd);
            return 1;
        }

        try
        {
            var pasted = UiaHelper.PasteTextToFocus(text, restoreDelayMs);
            JsonOutput.WriteSuccess(new
            {
                method = "clipboard-paste-focus",
                length = text.Length,
                clipboardRestored = pasted.ClipboardRestored,
                clipboardSnapshotComplete = pasted.SnapshotComplete,
            });
            return 0;
        }
        catch (ClipboardUnavailableException ex)
        {
            JsonOutput.WriteError("clipboard-unavailable", ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// scroll --hwnd h --direction up|down|left|right [--clicks n] [--x n --y n]
    /// [--allowOutsideWindow]: sends mouse-wheel notches at a screen point, which is the center of
    /// the window unless --x/--y are given. Unlike scroll-container and scroll-into-view it does
    /// not use UI Automation, so it also works for apps whose content UIA cannot see.
    /// </summary>
    public static int Scroll(Dictionary<string, string> opts)
    {
        if (!opts.ContainsKey("hwnd"))
        {
            JsonOutput.WriteError("invalid-argument", "scroll requires an explicit --hwnd.");
            return 1;
        }

        if (!opts.TryGetValue("direction", out var directionText)
            || directionText.ToLowerInvariant() is not ("up" or "down" or "left" or "right"))
        {
            JsonOutput.WriteError("invalid-argument", "--direction is required and must be one of: up, down, left, right.");
            return 1;
        }

        var direction = directionText.ToLowerInvariant();

        var clicks = 3;
        if (opts.TryGetValue("clicks", out var clicksText)
            && (!int.TryParse(clicksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out clicks)
                || clicks < 1 || clicks > 50))
        {
            JsonOutput.WriteError("invalid-argument", "--clicks must be an integer from 1 to 50.");
            return 1;
        }

        var hasX = opts.ContainsKey("x");
        var hasY = opts.ContainsKey("y");
        int pointX = 0, pointY = 0;
        if (hasX != hasY
            || (hasX && (!int.TryParse(opts["x"], NumberStyles.Integer, CultureInfo.InvariantCulture, out pointX)
                || !int.TryParse(opts["y"], NumberStyles.Integer, CultureInfo.InvariantCulture, out pointY))))
        {
            JsonOutput.WriteError("invalid-argument", "--x and --y must be given together and be valid integers.");
            return 1;
        }

        var allowOutsideWindow = false;
        if (opts.TryGetValue("allowOutsideWindow", out var allowText)
            && !bool.TryParse(allowText, out allowOutsideWindow))
        {
            JsonOutput.WriteError("invalid-argument", "--allowOutsideWindow must be true or false (a bare flag means true).");
            return 1;
        }

        if (opts.ContainsKey("allowOutsideWindow") && !hasX)
        {
            JsonOutput.WriteError("invalid-argument", "--allowOutsideWindow is only valid with --x/--y.");
            return 1;
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode, error!);
            return 1;
        }

        if (hasX
            && !TryValidateCoordinateTarget(windowHwnd, pointX, pointY, allowOutsideWindow, out var preflightError, skipIfMinimized: true))
        {
            JsonOutput.WriteError("invalid-argument", preflightError!);
            return 1;
        }

        if (!EnsureTargetWindowForeground(windowHwnd, restoreIfMinimized: true))
        {
            WriteForegroundFailure(windowHwnd);
            return 1;
        }

        if (!hasX)
        {
            // Default target: the center of the window as it is now, after activation or restore.
            if (!NativeMethods.GetWindowRect(windowHwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                JsonOutput.WriteError("invalid-argument", "Could not read a usable window rectangle; nothing was scrolled.");
                return 1;
            }

            pointX = rect.Left + ((rect.Right - rect.Left) / 2);
            pointY = rect.Top + ((rect.Bottom - rect.Top) / 2);
        }
        else if (!TryValidateCoordinateTarget(windowHwnd, pointX, pointY, allowOutsideWindow, out var boundsError))
        {
            JsonOutput.WriteError("invalid-argument", boundsError!);
            return 1;
        }

        var horizontal = direction is "left" or "right";
        var positive = direction is "up" or "right";
        NativeMethods.Scroll(pointX, pointY, horizontal, positive, clicks);
        JsonOutput.WriteSuccess(new { method = "synthetic-wheel", direction, clicks, x = pointX, y = pointY });
        return 0;
    }

    public static int GetText(Dictionary<string, string> opts)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var (element, elementErrorCode, elementError, elementErrorDetails) =
            ResolveElement(windowHwnd, opts, includeControlType: true);
        if (element is null)
        {
            JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
            return 1;
        }

        var text = UiaHelper.GetText(element);
        JsonOutput.WriteSuccess(new { text });
        return 0;
    }

    public static int GetState(Dictionary<string, string> opts,
        Func<IntPtr, AutomationElement?>? acquireWindow = null)
    {
        var validationError = ValidateStateTargetArguments(opts);
        if (validationError is not null)
        {
            JsonOutput.WriteError("invalid-argument", validationError);
            return 1;
        }

        try
        {
            var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
            if (errorCode is not null)
            {
                JsonOutput.WriteError(errorCode, error!);
                return 1;
            }

            var (element, elementErrorCode, elementError, elementErrorDetails) =
                ResolveElement(windowHwnd, opts, includeControlType: true,
                    propagateWindowErrors: true, acquireWindow: acquireWindow);
            if (element is null)
            {
                JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
                return 1;
            }

            return WriteStateResult(UiaHelper.ReadState(element));
        }
        catch (ElementNotAvailableException ex)
        {
            JsonOutput.WriteError("stale-context", ex.Message);
            return 1;
        }
        catch (Exception ex) when (UiaHelper.IsStateProviderError(ex))
        {
            JsonOutput.WriteError("state-provider-error", ex.Message);
            return 1;
        }
    }

    private static string? ValidateStateTargetArguments(Dictionary<string, string> opts)
    {
        var (_, _, selectorErrorCode, selectorError) = ParseSelectorArgs(opts);
        var (_, controlTypeError) = ParseOptionalControlType(opts);
        if (selectorErrorCode is not null || controlTypeError is not null)
        {
            return selectorError ?? controlTypeError!;
        }

        foreach (var key in new[] { "hwnd", "scopeHwnd" })
        {
            if (!opts.TryGetValue(key, out var hwnd))
            {
                continue;
            }

            try
            {
                UiaHelper.ParseHwnd(hwnd);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                return $"Invalid {key} '{hwnd}'.";
            }
        }

        return null;
    }

    public static int SetToggleState(Dictionary<string, string> opts,
        Func<IntPtr, AutomationElement?>? acquireWindow = null)
    {
        opts.TryGetValue("desiredState", out var desiredState);
        opts.TryGetValue("expectedState", out var expectedState);
        if (!UiaHelper.IsToggleStateName(desiredState) || !UiaHelper.IsToggleStateName(expectedState))
        {
            JsonOutput.WriteError("invalid-argument",
                "--desiredState and --expectedState are required and must be on, off or indeterminate.",
                new { mutationAttempted = false });
            return 1;
        }
        var validationError = ValidateStateTargetArguments(opts);
        if (validationError is not null)
        {
            JsonOutput.WriteError("invalid-argument", validationError, new { mutationAttempted = false });
            return 1;
        }

        AutomationElement target;
        try
        {
            var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
            if (errorCode is not null)
            {
                JsonOutput.WriteError(errorCode, error!, new { mutationAttempted = false });
                return 1;
            }

            var (element, elementErrorCode, elementError, elementErrorDetails) =
                ResolveElement(windowHwnd, opts, includeControlType: true,
                    propagateWindowErrors: true, acquireWindow: acquireWindow);
            if (element is null)
            {
                JsonOutput.WriteError(elementErrorCode!, elementError!, elementErrorDetails);
                return 1;
            }

            target = element;
        }
        catch (ElementNotAvailableException ex)
        {
            JsonOutput.WriteError("stale-context", ex.Message, new { mutationAttempted = false });
            return 1;
        }
        catch (Exception ex) when (UiaHelper.IsStateProviderError(ex))
        {
            JsonOutput.WriteError("state-provider-error", ex.Message, new { mutationAttempted = false });
            return 1;
        }

        return WriteToggleStateResult(desiredState, expectedState,
            UiaHelper.SetToggleState(target, desiredState, expectedState));
    }

    internal static int WriteToggleStateResult(string desiredState, string expectedState,
        UiaHelper.ToggleStateChangeResult result)
    {
        var payload = new { desiredState, expectedState, before = result.Before, after = result.After,
            mutationAttempted = result.MutationAttempted };
        if (result.Error is not null)
        {
            JsonOutput.WriteError(result.Error, result.Message!, payload);
            return 1;
        }

        JsonOutput.WriteSuccess(payload);
        return 0;
    }

    internal static int WriteStateResult(ElementStateInfo state)
    {
        if (UiaHelper.StateReadError(state) is { } error)
        {
            JsonOutput.WriteError(error.Error, error.Message, new { state });
            return 1;
        }

        JsonOutput.WriteSuccess(new { state });
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

        var (controlType, controlTypeError) = ParseOptionalControlType(opts);
        if (controlTypeError is not null)
        {
            JsonOutput.WriteError("invalid-argument", controlTypeError);
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
                element = UiaHelper.ResolveSelector(scope, selector, controlType);
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
                    $"Element [{strategy}] '{value}' did not reach state '{state}' within {timeoutMs}ms.",
                    element is null ? new { lookupDiagnostics = CreateLookupDiagnostics(windowHwnd) } : null);
                return 1;
            }

            Thread.Sleep(pollMs);
        }
    }

    public static int ReadVisibleText(Dictionary<string, string> opts)
    {
        if (!InspectionLimits.TryParse(opts, out var limits, out var budgetError))
        {
            JsonOutput.WriteError("invalid-argument", budgetError!);
            return 1;
        }
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
            NativeMethods.EnsureWindowExists(windowHwnd);
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

        var depthStats = new DepthLimitStats();
        object? traversalReport = null;
        List<string> lines;
        bool truncated;
        if (limits.IsBounded)
        {
            var traversal = new InspectionTraversal(limits);
            lines = UiaHelper.CollectBoundedText(element, maxDepth ?? UiaHelper.DefaultTextMaxDepth, traversal, depthStats);
            traversalReport = traversal.Report;
            truncated = traversal.Truncated;
            if (traversal.NodesCaptured == 0)
            {
                return WriteIncompleteInspection(traversalReport, lines);
            }
        }
        else (lines, truncated) = UiaHelper.CollectVisibleText(element, maxDepth, depthStats);
        var limited = depthStats.IsLimited;
        var usedDepth = maxDepth ?? UiaHelper.DefaultTextMaxDepth;
        JsonOutput.WriteSuccess(new
        {
            lines,
            truncated,
            traversal = traversalReport,
            depthLimited = limited ? true : (bool?)null,
            maxDepth = limited ? usedDepth : (int?)null,
            nodesBeyondDepth = limited ? depthStats.NodesBeyondDepth : (int?)null,
            nodesBeyondDepthCapped = limited && depthStats.Capped ? true : (bool?)null,
            hint = limited ? depthStats.Hint(usedDepth) : null,
        });
        return 0;
    }

    internal static int ReadPage(Dictionary<string, string> opts, bool tree)
    {
        if (!PageReadOptions.TryParse(opts, tree, out var options, out var error))
        { JsonOutput.WriteError("invalid-argument", error!); return 1; }
        if (ValidateStructuredTargetArguments(opts) is { } validationError)
        { JsonOutput.WriteError("invalid-argument", validationError); return 1; }
        try
        {
            var resolved = ResolveStructuredTarget(opts);
            if (resolved.element is null)
            { JsonOutput.WriteError(resolved.errorCode!, resolved.error!, resolved.details); return 1; }
            var budget = new InspectionTraversal(options!.Budget);
            var reader = new PagedUiaReader<AutomationElement>(new UiaPageSource(budget), options, tree, budget);
            return WritePage(reader.Read(resolved.element), reader.Error);
        }
        catch (ElementNotAvailableException ex) { JsonOutput.WriteError("stale-context", ex.Message); return 1; }
        catch (Exception ex) when (UiaHelper.IsStateProviderError(ex))
        { JsonOutput.WriteError("page-provider-error", ex.Message); return 1; }
    }

    private static string? ValidateStructuredTargetArguments(Dictionary<string, string> opts)
    {
        foreach (var key in new[] { "hwnd", "scopeHwnd" })
        {
            if (!opts.TryGetValue(key, out var text)) continue;
            try { UiaHelper.ParseHwnd(text); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            { return $"Invalid {key} '{text}'."; }
        }
        return opts.ContainsKey("strategy") ? ValidateStateTargetArguments(opts) : null;
    }

    private static (AutomationElement? element, string? errorCode, string? error, object? details) ResolveStructuredTarget(Dictionary<string, string> opts)
    {
        var (hwnd, code, message) = ResolveWindowHwnd(opts);
        if (code is not null) return (null, code, message, null);
        if (opts.ContainsKey("strategy")) return ResolveElement(hwnd, opts, includeControlType: true, propagateWindowErrors: true);
        var root = opts.TryGetValue("scopeHwnd", out var scope) ? UiaHelper.ParseHwnd(scope) : hwnd;
        var validated = ValidateResolvedWindowHwnd(root);
        if (validated.errorCode is not null) return (null, validated.errorCode, validated.error, null);
        var target = UiaHelper.FindWindowByHwnd($"0x{root.ToInt64():X}", propagateErrors: true);
        return target is null ? (null, "element-not-found", "No UIA root was returned for the requested handle.", null) : (target, null, null, null);
    }

    internal static int Select(Dictionary<string, string> opts, bool row)
    {
        if (!SelectionOptions.TryParse(opts, row, out var options, out var error))
        { JsonOutput.WriteError("invalid-argument", error!, new { actionAttempted = false }); return 1; }
        if (ValidateStructuredTargetArguments(opts) is { } validationError)
        { JsonOutput.WriteError("invalid-argument", validationError, new { actionAttempted = false }); return 1; }
        var budget = new InspectionTraversal(options!.Bounds.Budget);
        var source = new UiaSelectionSource(() =>
        {
            try
            {
                var target = ResolveStructuredTarget(opts);
                return new SelectionResolution<AutomationElement>(target.element, target.errorCode, target.error, target.details);
            }
            catch (WindowGoneException ex) { return new(null, "window-gone", ex.Message); }
        }, options, budget);
        var selection = new VerifiedSelection<AutomationElement>(source, budget);
        var result = selection.Run(new { operation = row ? "select-row" : "select-item", row = row ? options.Bounds.Offset : (int?)null,
            rowKind = options.RowKind, maxDepth = row ? options.Bounds.MaxDepth : (int?)null });
        return WriteSelection(result, selection.Error);
    }

    internal static int WriteSelection(object result, string? error)
    {
        if (error is null) { JsonOutput.WriteSuccess(result); return 0; }
        JsonOutput.WriteError(error, "Selection was not verified; inspect actionAttempted, partial observations and mutation uncertainty before retrying.", result);
        return 1;
    }

    internal static int WritePage(object page, string? error)
    {
        if (error is null) { JsonOutput.WriteSuccess(page); return 0; }
        JsonOutput.WriteError(error, "Page observation failed or changed; examine partial records, traversal and retry guidance.", page);
        return 1;
    }

    internal static int WriteIncompleteInspection(object traversal, List<string>? lines = null)
    {
        JsonOutput.WriteError("inspection-incomplete", "No complete root node snapshot was captured; inspect traversal for limits or provider errors.", new { traversal, lines });
        return 1;
    }

    /// <summary>Captures a window PNG, optionally cropped/scaled, with physical bounds and saved image dimensions.</summary>
    public static int Screenshot(Dictionary<string, string> opts)
    {
        System.Drawing.Rectangle? region = null;
        if (opts.TryGetValue("region", out var regionText))
        {
            var parts = regionText.Split(',');
            if (parts.Length != 4
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                JsonOutput.WriteError("invalid-argument", "--region must be x,y,width,height using four integers.");
                return 1;
            }

            region = new System.Drawing.Rectangle(x, y, width, height);
        }

        double? scale = null;
        if (opts.TryGetValue("scale", out var scaleText))
        {
            if (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScale))
            {
                JsonOutput.WriteError("invalid-argument", "--scale must be a finite number from 0.1 to 1.0.");
                return 1;
            }

            scale = parsedScale;
        }

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
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        WindowScreenshotResult screenshot;
        try
        {
            screenshot = ScreenshotHelper.CaptureWindow(windowHwnd, region, scale);
        }
        catch (ArgumentException ex) when (ex.ParamName is "region" or "scale")
        {
            JsonOutput.WriteError("invalid-argument", ex.Message);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
            // The window still exists, but its capture metadata was unavailable.
            JsonOutput.WriteError("stale-context", ex.Message);
            return 1;
        }

        JsonOutput.WriteSuccess(new
        {
            screenshotPath = screenshot.ScreenshotPath,
            windowRect = new
            {
                left = screenshot.WindowRect.Left,
                top = screenshot.WindowRect.Top,
                right = screenshot.WindowRect.Right,
                bottom = screenshot.WindowRect.Bottom,
            },
            imageWidth = screenshot.ImageWidth,
            imageHeight = screenshot.ImageHeight,
            dpiScale = screenshot.DpiScale,
            region = screenshot.Region is { } capturedRegion ? new
            {
                x = capturedRegion.X,
                y = capturedRegion.Y,
                width = capturedRegion.Width,
                height = capturedRegion.Height,
            } : null,
            scale = screenshot.Scale,
        });
        return 0;
    }

    /// <summary>
    /// Brings a window to the foreground. Unlike every Phase 8 verb, this is intentionally
    /// interactive/non-read-only: it changes window activation/z-order/focus state rather than
    /// only observing it.
    /// </summary>
    public static int Activate(Dictionary<string, string> opts)
    {
        var restore = false;
        if (opts.TryGetValue("restore", out var restoreText) && !bool.TryParse(restoreText, out restore))
        {
            JsonOutput.WriteError("invalid-argument", "--restore must be true or false.");
            return 1;
        }

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

        var (_, windowErrorCode, windowError) = ValidateResolvedWindowHwnd(windowHwnd);
        if (windowErrorCode is not null)
        {
            JsonOutput.WriteError(windowErrorCode, windowError!,
                restore ? new { restoreRequested = true, restoreAttempted = false } : null);
            return 1;
        }

        if (restore)
        {
            var root = NativeMethods.GetAncestor(windowHwnd, NativeMethods.GA_ROOT);
            var (_, rootErrorCode, rootError) = ValidateResolvedWindowHwnd(root);
            if (rootErrorCode is not null)
            {
                JsonOutput.WriteError(rootErrorCode, rootError!,
                    new { restoreRequested = true, restoreAttempted = false });
                return 1;
            }

            var result = ActivateRestoringWindow(() => ReadMinimizedState(root),
                onRestoreAttempt => EnsureTargetWindowForeground(root, restoreIfMinimized: true, onRestoreAttempt));
            return WriteActivationRestoreResult(root, result);
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
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError(
                "stale-context",
                $"Could not bring hwnd '0x{windowHwnd.ToInt64():X}' to the foreground. The window " +
                "may have closed, or Windows denied the foreground switch (focus-stealing " +
                "prevention) — this is not necessarily fatal; retry or activate manually.",
                ForegroundDiagnostics.Attach(windowHwnd));
            return 1;
        }

        JsonOutput.WriteSuccess(new { activated = true });
        return 0;
    }

    internal static ActivationRestoreResult ActivateRestoringWindow(Func<bool?> readMinimized,
        Func<Action, bool> activate)
    {
        var before = readMinimized();
        var attempted = false;
        if (before is null)
        {
            return new(null, null, false, false, "window-gone", "The target window no longer exists; rediscover its HWND.");
        }

        try
        {
            var activated = activate(() => attempted = true);
            var after = readMinimized();
            if (after is null)
            {
                return new(before, null, attempted, false, "window-gone", "The target window disappeared during activation; rediscover its HWND.");
            }
            if (after.Value)
            {
                return new(before, after, attempted, false, "stale-context", "The target is still minimized; restoration was not verified.");
            }
            if (!activated)
            {
                return new(before, after, attempted, false, "stale-context",
                    "Foreground activation was not verified. Windows may deny foreground activation; check the target manually before continuing.");
            }

            return new(before, after, attempted, true);
        }
        catch (WindowGoneException ex)
        {
            return new(before, readMinimized(), attempted, false, "window-gone", ex.Message);
        }
        catch (ForegroundActivationException ex)
        {
            var after = readMinimized();
            return new(before, after, attempted, false, after is null ? "window-gone" : "stale-context", ex.Message);
        }
    }

    internal sealed record ActivationRestoreResult(bool? IsMinimizedBefore, bool? IsMinimizedAfter,
        bool RestoreAttempted, bool Activated, string? Error = null, string? Message = null);

    internal static int WriteActivationRestoreResult(IntPtr root, ActivationRestoreResult result,
        IForegroundDiagnosticSource? source = null)
    {
        var payload = new { rootHwnd = $"0x{root.ToInt64():X}", restoreRequested = true,
            restoreAttempted = result.RestoreAttempted, isMinimizedBefore = result.IsMinimizedBefore,
            isMinimizedAfter = result.IsMinimizedAfter, activated = result.Activated };
        if (result.Error is not null)
        {
            JsonOutput.WriteError(result.Error, result.Message!, result.Error == "stale-context"
                ? ForegroundDiagnostics.Attach(root, payload, source) : payload);
            return 1;
        }

        JsonOutput.WriteSuccess(payload);
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
            try
            {
                NativeMethods.EnsureWindowExists(windowHwnd);
            }
            catch (WindowGoneException ex)
            {
                JsonOutput.WriteError("window-gone", ex.Message, new { step = "resolve-window" });
                return 1;
            }

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
                new { step = "resolve-input", lookupDiagnostics = CreateLookupDiagnostics(windowHwnd) });
            return 1;
        }

        var activationHwnd = windowHwnd;
        if (!TryEnsureChatInputForeground(() => EnsureElementForeground(inputElement, windowHwnd, out activationHwnd),
            windowHwnd, readEffectiveTarget: () => activationHwnd))
        {
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
                UiaHelper.SendKeys(inputElement, keys, UiaHelper.KeyFocusMode.None);
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

    internal static void WriteForegroundFailure(IntPtr targetHwnd,
        string message = "Could not bring the target window to the foreground.", object? extra = null,
        IForegroundDiagnosticSource? source = null)
    {
        JsonOutput.WriteError("foreground-activation-failed", message, ForegroundDiagnostics.Attach(targetHwnd, extra, source));
    }

    internal static bool TryEnsureChatInputForeground(Func<bool> ensureForeground, IntPtr targetHwnd = default,
        IForegroundDiagnosticSource? source = null, Func<IntPtr>? readEffectiveTarget = null)
    {
        bool foreground;
        try
        {
            foreground = ensureForeground();
        }
        catch (WindowGoneException ex)
        {
            JsonOutput.WriteError("window-gone", ex.Message, new { step = "resolve-input" });
            return false;
        }

        if (!foreground)
        {
            WriteForegroundFailure(readEffectiveTarget?.Invoke() ?? targetHwnd, extra: new { step = "resolve-input" }, source: source);
        }

        return foreground;
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
        var (scope, scopeErrorCode, scopeError) = ResolveFindScope(opts, out var diagnosticHwnd);
        if (scope is null)
        {
            JsonOutput.WriteError(scopeErrorCode!, scopeError!, scopeErrorCode == "element-not-found"
                ? new { lookupDiagnostics = CreateLookupDiagnostics(diagnosticHwnd) } : null);
            return 1;
        }

        var (strategy, value, argErrorCode, argError) = ParseSelectorArgs(opts);
        if (argErrorCode is not null)
        {
            JsonOutput.WriteError(argErrorCode, argError!);
            return 1;
        }

        var (controlType, controlTypeError) = ParseOptionalControlType(opts);
        if (controlTypeError is not null)
        {
            JsonOutput.WriteError("invalid-argument", controlTypeError);
            return 1;
        }

        var match = UiaHelper.ResolveSelector(
            scope, new Selector { Strategy = strategy, Value = value! }, controlType);
        if (match is null)
        {
            JsonOutput.WriteSuccess(new { found = false, lookupDiagnostics = CreateLookupDiagnostics(diagnosticHwnd) });
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
        var (scope, scopeErrorCode, scopeError) = ResolveFindScope(opts, out var diagnosticHwnd);
        if (scope is null)
        {
            JsonOutput.WriteError(scopeErrorCode!, scopeError!, scopeErrorCode == "element-not-found"
                ? new { lookupDiagnostics = CreateLookupDiagnostics(diagnosticHwnd) } : null);
            return 1;
        }

        var (strategy, value, argErrorCode, argError) = ParseSelectorArgs(opts);
        if (argErrorCode is not null)
        {
            JsonOutput.WriteError(argErrorCode, argError!);
            return 1;
        }

        var (controlType, controlTypeError) = ParseOptionalControlType(opts);
        if (controlTypeError is not null)
        {
            JsonOutput.WriteError("invalid-argument", controlTypeError);
            return 1;
        }

        opts.TryGetValue("excludeValue", out var excludeValue);

        var matches = UiaHelper.ResolveSelectorAll(
            scope, new Selector { Strategy = strategy, Value = value! }, controlType);
        var elements = new List<object>();
        foreach (AutomationElement match in matches)
        {
            if (excludeValue is not null && match.Current.Name == excludeValue)
            {
                continue;
            }

            elements.Add(ToElementSummary(match));
        }

        JsonOutput.WriteSuccess(new { count = elements.Count, elements,
            lookupDiagnostics = elements.Count == 0 ? CreateLookupDiagnostics(diagnosticHwnd) : null });
        return 0;
    }

    internal static double PollForScrollPosition(
        Func<double> readPosition,
        double requestedPercent,
        Func<long> elapsedMilliseconds,
        Action<int> sleep)
    {
        var percentAfter = -1d;
        while (elapsedMilliseconds() < 500)
        {
            percentAfter = readPosition();
            if (Math.Abs(percentAfter - requestedPercent) < 0.01)
            {
                break;
            }

            var remainingMilliseconds = 500 - elapsedMilliseconds();
            if (remainingMilliseconds <= 0)
            {
                break;
            }

            sleep((int)Math.Min(25, remainingMilliseconds));
        }

        return percentAfter;
    }

    public static int ScrollContainer(Dictionary<string, string> opts)
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

        if (!opts.TryGetValue("verticalPercent", out var percentText)
            || !double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var requestedPercent)
            || !double.IsFinite(requestedPercent)
            || requestedPercent is < 0 or > 100)
        {
            JsonOutput.WriteError("invalid-argument", "--verticalPercent is required and must be a number between 0 and 100.");
            return 1;
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        AutomationElement? container = null;
        var percentBefore = -1d;
        var stage = "resolve-window";
        try
        {
            var window = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
            if (window is null)
            {
                JsonOutput.WriteError("stale-context", "The target window is no longer available.");
                return 1;
            }

            var scope = window;
            var diagnosticHwnd = windowHwnd;
            if (opts.TryGetValue("scopeHwnd", out var scopeHwndText))
            {
                IntPtr scopeHwnd;
                try
                {
                    scopeHwnd = UiaHelper.ParseHwnd(scopeHwndText);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    JsonOutput.WriteError("invalid-argument", $"Invalid scopeHwnd '{scopeHwndText}'.");
                    return 1;
                }

                var (_, scopeErrorCode, scopeError) = ValidateResolvedWindowHwnd(scopeHwnd);
                if (scopeErrorCode is not null)
                {
                    JsonOutput.WriteError(scopeErrorCode, scopeError!);
                    return 1;
                }

                scope = UiaHelper.FindWindowByHwnd($"0x{scopeHwnd.ToInt64():X}");
                diagnosticHwnd = scopeHwnd;
                if (scope is null)
                {
                    JsonOutput.WriteError("container-not-found", "No scope element found for --scopeHwnd.",
                        new { lookupDiagnostics = CreateLookupDiagnostics(diagnosticHwnd) });
                    return 1;
                }
            }

            stage = "resolve-container";
            var selector = new Selector { Strategy = strategy, Value = value };
            var matches = UiaHelper.ResolveSelectorAll(scope, selector);
            if (matches.Count == 0)
            {
                JsonOutput.WriteError("container-not-found", $"No container found for selector [{strategy}] '{value}'.",
                    new { lookupDiagnostics = CreateLookupDiagnostics(diagnosticHwnd) });
                return 1;
            }

            if (matches.Count > 1)
            {
                var (candidates, limitMessage) = CreateCandidates(matches);
                JsonOutput.WriteError("ambiguous-container",
                    $"Container selector [{strategy}] '{value}' matched {matches.Count} elements.{limitMessage} Narrow the selector or --scopeHwnd.",
                    new { count = matches.Count, candidates });
                return 1;
            }

            container = matches[0];
            stage = "get-scroll-pattern";
            var scroll = UiaHelper.GetScrollPattern(container);
            if (scroll is null || !scroll.Current.VerticallyScrollable)
            {
                JsonOutput.WriteError("not-scrollable", "The selected container does not support vertical UI Automation scrolling.",
                    new { verticalScrollPercentBefore = -1d, verticalScrollPercentAfter = -1d });
                return 1;
            }

            percentBefore = UiaHelper.GetVerticalScrollPercent(container);
            stage = "set-scroll-percent";
            var alreadyAtRequestedPosition = Math.Abs(percentBefore - requestedPercent) < 0.01;
            if (!alreadyAtRequestedPosition)
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, requestedPercent);
            }

            var percentAfter = percentBefore;
            if (!alreadyAtRequestedPosition)
            {
                stage = "wait-for-position";
                var pollClock = Stopwatch.StartNew();
                percentAfter = PollForScrollPosition(
                    () => UiaHelper.GetVerticalScrollPercent(container),
                    requestedPercent,
                    () => pollClock.ElapsedMilliseconds,
                    Thread.Sleep);
            }

            var boundaryReached = requestedPercent == 0 && percentAfter == 0
                || requestedPercent == 100 && percentAfter == 100;
            JsonOutput.WriteSuccess(new
            {
                container = ToScrollContainerSummary(container),
                verticalScrollPercentBefore = percentBefore,
                verticalScrollPercentAfter = percentAfter,
                boundaryReached,
                changed = Math.Abs(percentAfter - percentBefore) >= 0.01,
            });
            return 0;
        }
        catch (ElementNotAvailableException ex)
        {
            JsonOutput.WriteError("stale-context", $"The selected UI Automation container became unavailable during '{stage}': {ex.Message}",
                new
                {
                    details = new { stage },
                    verticalScrollPercentBefore = percentBefore,
                    verticalScrollPercentAfter = SafeVerticalScrollPercent(container),
                });
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            JsonOutput.WriteError("scroll-provider-error", $"The UI Automation provider failed during '{stage}': {ex.Message}",
                new
                {
                    details = new { stage },
                    verticalScrollPercentBefore = percentBefore,
                    verticalScrollPercentAfter = SafeVerticalScrollPercent(container),
                });
            return 1;
        }
    }

    /// <summary>
    /// Brings an element into view using UI Automation patterns only: it does not request
    /// activation and makes no cursor or input calls (the target's UIA provider may still activate
    /// its window during a scroll). Stages: element already in the tree (already-visible,
    /// scroll-item, ancestor-scroll); ItemContainerPattern lookup (item-container); vertical
    /// scroll-search from the top (scroll-search). The scroll position is not restored afterwards.
    /// </summary>
    public static int ScrollIntoView(Dictionary<string, string> opts)
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

        var hasScopeStrategy = opts.TryGetValue("scopeStrategy", out var scopeStrategyText);
        var hasScopeValue = opts.TryGetValue("scopeValue", out var scopeValue);
        if (hasScopeStrategy != hasScopeValue)
        {
            JsonOutput.WriteError("invalid-argument", "--scopeStrategy and --scopeValue must both be supplied, or neither.");
            return 1;
        }

        var scopeStrategy = default(SelectorStrategy);
        if (hasScopeStrategy
            && !UiaHelper.TryParseImplementedSelectorStrategy(scopeStrategyText, out scopeStrategy, out var scopeStrategyError))
        {
            JsonOutput.WriteError("invalid-argument", $"--scopeStrategy {scopeStrategyError}");
            return 1;
        }

        var step = ScrollAmount.LargeIncrement;
        if (opts.TryGetValue("step", out var stepText))
        {
            if (stepText is not ("small" or "large"))
            {
                JsonOutput.WriteError("invalid-argument", "--step must be one of: small, large.");
                return 1;
            }

            step = stepText == "small" ? ScrollAmount.SmallIncrement : ScrollAmount.LargeIncrement;
        }

        var maxScrolls = 100;
        if (opts.TryGetValue("maxScrolls", out var maxScrollsText)
            && (!int.TryParse(maxScrollsText, out maxScrolls) || maxScrolls < 1 || maxScrolls > 1000))
        {
            JsonOutput.WriteError("invalid-argument", "--maxScrolls must be an integer between 1 and 1000.");
            return 1;
        }

        if (opts.TryGetValue("timeoutMs", out var tText)
            && (!int.TryParse(tText, out var parsedTimeoutMs) || parsedTimeoutMs < 0))
        {
            JsonOutput.WriteError("invalid-argument", "--timeoutMs must be a non-negative integer.");
            return 1;
        }

        var timeoutMs = opts.TryGetValue("timeoutMs", out var t2) ? int.Parse(t2) : 10000;
        var clock = Stopwatch.StartNew();
        bool IsTimedOut() => clock.ElapsedMilliseconds >= timeoutMs;
        int GetRemainingMs()
        {
            var remaining = timeoutMs - clock.ElapsedMilliseconds;
            return remaining > 0 ? (int)Math.Min(remaining, int.MaxValue) : 0;
        }

        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        if (errorCode is not null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
            JsonOutput.WriteError("window-not-responding", "The target window is not responding.");
            return 1;
        }

        var context = new ScrollContext();
        try
        {
            return ScrollIntoViewCore(
                windowHwnd, new Selector { Strategy = strategy, Value = value },
                hasScopeStrategy ? new Selector { Strategy = scopeStrategy, Value = scopeValue! } : null,
                step, maxScrolls, timeoutMs, IsTimedOut, GetRemainingMs, context);
        }
        catch (ElementNotAvailableException ex)
        {
            JsonOutput.WriteError("stale-context",
                $"A UI Automation element became unavailable while scrolling: {ex.Message}",
                new
                {
                    verticalScrollPercentBefore = context.PercentBefore,
                    verticalScrollPercentAfter = SafeVerticalScrollPercent(context.Scroller),
                });
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            // Provider failures (e.g. SetScrollPercent/Scroll/ScrollIntoView/Realize on a disabled or unsupported container, or an empty bounding rectangle during a visibility check).
            // ElementNotAvailableException is a separate type and is handled by the catch above.
            var stage = ex is UiaHelper.EmptyBoundsException ? "visibility-check" : context.Stage;
            JsonOutput.WriteError("scroll-provider-error",
                $"The UI Automation provider failed during '{stage}': {ex.Message}",
                new
                {
                    details = new { stage },
                    verticalScrollPercentBefore = context.PercentBefore,
                    verticalScrollPercentAfter = SafeVerticalScrollPercent(context.Scroller),
                });
            return 1;
        }
    }

    private sealed class ScrollContext
    {
        public string Stage { get; set; } = "resolve-window";

        public AutomationElement? Scroller { get; set; }

        public double PercentBefore { get; set; } = -1;

        public void Track(string stage, AutomationElement? scroller, double percentBefore)
        {
            Stage = stage;
            Scroller = scroller;
            PercentBefore = percentBefore;
        }
    }

    private static double SafeVerticalScrollPercent(AutomationElement? scroller)
    {
        try
        {
            return UiaHelper.GetVerticalScrollPercent(scroller);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            return -1;
        }
    }

    private static int ScrollIntoViewCore(
        IntPtr windowHwnd, Selector selector, Selector? scopeSelector,
        ScrollAmount step, int maxScrolls, int timeoutMs, Func<bool> isTimedOut, Func<int> getRemainingMs,
        ScrollContext context)
    {
        var window = UiaHelper.FindWindowByHwnd($"0x{windowHwnd.ToInt64():X}");
        if (window is null)
        {
            JsonOutput.WriteError("element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.");
            return 1;
        }

        var scope = window;
        if (scopeSelector is not null)
        {
            context.Track("resolve-scope", null, -1);
            var scopeMatches = UiaHelper.ResolveSelectorAll(window, scopeSelector);
            if (scopeMatches.Count == 0)
            {
                JsonOutput.WriteError("container-not-found",
                    $"No scope element found for --scopeStrategy '{scopeSelector.Strategy}' --scopeValue '{scopeSelector.Value}'.",
                    new { verticalScrollPercentBefore = -1d, verticalScrollPercentAfter = -1d });
                return 1;
            }

            if (scopeMatches.Count > 1)
            {
                var (scopeCandidates, scopeLimitMessage) = CreateCandidates(scopeMatches);
                JsonOutput.WriteError("ambiguous-container",
                    $"Scope selector [{scopeSelector.Strategy}] '{scopeSelector.Value}' matched {scopeMatches.Count} elements.{scopeLimitMessage} Narrow --scopeStrategy/--scopeValue.",
                    new
                    {
                        count = scopeMatches.Count,
                        candidates = scopeCandidates,
                        verticalScrollPercentBefore = -1d,
                        verticalScrollPercentAfter = -1d,
                    });
                return 1;
            }

            scope = scopeMatches[0];
        }

        // Stage 1: the target is already in the UIA tree.
        context.Track("resolve-target", scope, -1);
        var scopePercentBefore = UiaHelper.GetVerticalScrollPercent(scope);
        context.Track("resolve-target", scope, scopePercentBefore);
        var matches = UiaHelper.ResolveSelectorAll(scope, selector);
        if (matches.Count > 1)
        {
            WriteScrollAmbiguousMatch(selector, matches, scope, scopePercentBefore);
            return 1;
        }

        if (matches.Count == 1)
        {
            var element = matches[0];
            var ancestor = UiaHelper.FindScrollableAncestor(element, scope);
            var percentBefore = UiaHelper.GetVerticalScrollPercent(ancestor);
            context.Track("resolve-target", ancestor, percentBefore);
            if (UiaHelper.IsFullyVisibleWithArea(element, ancestor))
            {
                WriteScrollSuccess("already-visible", 0, false, element, ancestor, percentBefore, scope);
                return 0;
            }

            context.Stage = "scroll-item";
            var hasScrollItem = UiaHelper.TryScrollItemIntoView(element);
            if (hasScrollItem)
            {
                var refreshed = ResolveSingle(scope, selector);
                var refreshedAncestor = refreshed is null
                    ? ancestor
                    : UiaHelper.FindScrollableAncestor(refreshed, scope) ?? ancestor;
                if (refreshed is not null && UiaHelper.IsFullyVisibleWithArea(refreshed, refreshedAncestor))
                {
                    WriteScrollSuccess("scroll-item", 0, false, refreshed, refreshedAncestor, percentBefore, scope);
                    return 0;
                }
            }

            var ancestorScroll = UiaHelper.GetScrollPattern(ancestor);
            if (ancestor is null || ancestorScroll is null || !ancestorScroll.Current.VerticallyScrollable)
            {
                WriteNotScrollable(
                    hasScrollItem
                        ? "The element supports ScrollItemPattern but is still not visible, and it has no vertically scrollable ancestor."
                        : "The element is off-view and has no ScrollItemPattern or vertically scrollable ancestor.",
                    ancestor, percentBefore);
                return 1;
            }

            context.Stage = "ancestor-scroll";
            var (stop, steps) = UiaHelper.StepAncestorUntilVisible(
                element, ancestor, ancestorScroll, step, maxScrolls, isTimedOut);
            if (stop == UiaHelper.AncestorScrollStop.Timeout)
            {
                WriteScrollTimeout(selector, timeoutMs, steps, ancestor, percentBefore);
                return 1;
            }

            if (stop != UiaHelper.AncestorScrollStop.Visible)
            {
                WriteNotFoundAfterScroll(selector, steps, stop == UiaHelper.AncestorScrollStop.ReachedEnd, ancestor, percentBefore,
                    "the element exists but could not be brought fully into view");
                return 1;
            }

            WriteScrollSuccess("ancestor-scroll", steps, false, element, ancestor, percentBefore, scope);
            return 0;
        }

        // Stage 2: the target is not in the tree (e.g. virtualized); pick the container.
        var container = scope;
        if (scopeSelector is null)
        {
            context.Track("container-detect", null, -1);
            var scrollables = UiaHelper.FindVerticallyScrollableDescendants(window);
            if (scrollables.Count == 0)
            {
                WriteNotScrollable("The target was not found and the window has no vertically scrollable container.", null, -1);
                return 1;
            }

            if (scrollables.Count > 1)
            {
                const int candidateLimit = 10;
                var candidates = scrollables.Take(candidateLimit).Select(ToElementSummary).ToArray();
                var limitMessage = scrollables.Count > candidateLimit
                    ? $" Only the first {candidateLimit} candidates are shown."
                    : string.Empty;
                JsonOutput.WriteError("ambiguous-container",
                    $"The target was not found and the window has {scrollables.Count} vertically scrollable containers.{limitMessage} Pass --scopeStrategy/--scopeValue to choose one.",
                    new
                    {
                        count = scrollables.Count,
                        candidates,
                        verticalScrollPercentBefore = -1d,
                        verticalScrollPercentAfter = -1d,
                    });
                return 1;
            }

            container = scrollables[0];
        }

        context.Track("item-container", container, -1);
        var containerPercentBefore = UiaHelper.GetVerticalScrollPercent(container);
        context.Track("item-container", container, containerPercentBefore);
        var item = UiaHelper.TryFindAndRealizeContainerItem(container, selector);
        if (item is not null)
        {
            var realized = UiaHelper.ResolveSelectorAll(container, selector);
            if (realized.Count > 1)
            {
                WriteScrollAmbiguousMatch(selector, realized, container, containerPercentBefore);
                return 1;
            }

            if (realized.Count == 1)
            {
                context.Stage = "scroll-item";
                if (UiaHelper.TryScrollItemIntoView(realized[0]))
                {
                    var refreshed = ResolveSingle(container, selector) ?? realized[0];
                    if (UiaHelper.IsFullyVisibleWithArea(refreshed, container))
                    {
                        WriteScrollSuccess("item-container", 0, false, refreshed, container, containerPercentBefore, scope);
                        return 0;
                    }
                }
            }
        }

        // Stage 3: scroll-search from the top.
        context.Stage = "scroll-search";
        var containerScroll = UiaHelper.GetScrollPattern(container);
        if (containerScroll is null || !containerScroll.Current.VerticallyScrollable)
        {
            WriteNotScrollable("The target was not found and the container has no vertically scrollable ScrollPattern.",
                container, containerPercentBefore);
            return 1;
        }

        var search = UiaHelper.ScrollSearch(
            containerScroll,
            step,
            () => { var found = UiaHelper.ResolveSelectorAll(container, selector); return found.Count == 0 ? null : found; },
            found => { if (found.Count == 1) { UiaHelper.TryScrollItemIntoView(found[0]); } },
            maxScrolls,
            isTimedOut,
            isSatisfied: found => found.Count > 1 || UiaHelper.IsFullyVisibleWithArea(found[0], container));

        var reachedEnd = search.Stop == UiaHelper.ScrollSearchStop.ReachedEnd;
        if (search.Stop == UiaHelper.ScrollSearchStop.Timeout)
        {
            WriteScrollTimeout(selector, timeoutMs, search.Steps, container, containerPercentBefore);
            return 1;
        }

        if (search.Stop == UiaHelper.ScrollSearchStop.Found || search.PartialResult is not null)
        {
            // A virtualizing container can briefly drop the realized item after ScrollIntoView;
            // retry the re-query before reporting it as not found.
            var found = search.Result ?? search.PartialResult;
            var retryTimedOut = false;
            for (var attempt = 0; (found is null || found.Count == 0) && attempt < 5; attempt++)
            {
                var remainingMs = getRemainingMs();
                if (remainingMs <= 0)
                {
                    retryTimedOut = true;
                    break;
                }

                Thread.Sleep(Math.Min(100, remainingMs));
                if (isTimedOut())
                {
                    retryTimedOut = true;
                    break;
                }

                var retry = UiaHelper.ResolveSelectorAll(container, selector);
                if (isTimedOut())
                {
                    retryTimedOut = true;
                    break;
                }

                found = retry.Count == 0 ? null : retry;
            }

            if (retryTimedOut)
            {
                WriteScrollTimeout(selector, timeoutMs, search.Steps, container, containerPercentBefore);
                return 1;
            }

            if (found is not null && found.Count > 1)
            {
                WriteScrollAmbiguousMatch(selector, found, container, containerPercentBefore);
                return 1;
            }

            if (found is not null && found.Count == 1)
            {
                var element = found[0];
                if (!UiaHelper.IsFullyVisibleWithArea(element, container)
                    && UiaHelper.TryScrollItemIntoView(element))
                {
                    var refreshed = UiaHelper.ResolveSelectorAll(container, selector);
                    if (refreshed.Count > 1)
                    {
                        WriteScrollAmbiguousMatch(selector, refreshed, container, containerPercentBefore);
                        return 1;
                    }

                    if (refreshed.Count == 1)
                    {
                        element = refreshed[0];
                    }
                }

                if (UiaHelper.IsFullyVisibleWithArea(element, container))
                {
                    WriteScrollSuccess("scroll-search", search.Steps, reachedEnd, element, container, containerPercentBefore, scope);
                    return 0;
                }

                WriteNotFoundAfterScroll(selector, search.Steps, reachedEnd, container, containerPercentBefore,
                    "the target was found but could not be brought fully into view");
                return 1;
            }

            WriteNotFoundAfterScroll(selector, search.Steps, reachedEnd, container, containerPercentBefore,
                "the target was found during the scroll but was no longer available afterwards");
            return 1;
        }

        WriteNotFoundAfterScroll(selector, search.Steps, reachedEnd, container, containerPercentBefore,
            reachedEnd ? "reached the end of the container" : "stopped at --maxScrolls");
        return 1;
    }

    private static void WriteNotFoundAfterScroll(
        Selector selector, int scrollSteps, bool reachedEnd, AutomationElement container, double percentBefore, string reason)
    {
        JsonOutput.WriteError("element-not-found-after-scroll",
            $"No element found in view for selector [{selector.Strategy}] '{selector.Value}' after {scrollSteps} scroll step(s); {reason}.",
            new
            {
                scrollSteps,
                reachedEnd,
                container = ToScrollContainerSummary(container),
                verticalScrollPercentBefore = percentBefore,
                verticalScrollPercentAfter = UiaHelper.GetVerticalScrollPercent(container),
            });
    }

    private static AutomationElement? ResolveSingle(AutomationElement scope, Selector selector)
    {
        var matches = UiaHelper.ResolveSelectorAll(scope, selector);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static void WriteScrollSuccess(
        string method, int scrollSteps, bool reachedEnd, AutomationElement element,
        AutomationElement? container, double percentBefore, AutomationElement scopeOrWindow)
    {
        var reportedContainer = container ?? scopeOrWindow;
        JsonOutput.WriteSuccess(new
        {
            method,
            scrollSteps,
            reachedEnd,
            fullyVisible = UiaHelper.IsFullyVisible(element, container),
            verticalScrollPercentBefore = percentBefore,
            verticalScrollPercentAfter = UiaHelper.GetVerticalScrollPercent(container),
            element = new
            {
                name = element.Current.Name ?? string.Empty,
                controlType = element.Current.ControlType?.ProgrammaticName ?? string.Empty,
                automationId = element.Current.AutomationId ?? string.Empty,
                isOffscreen = element.Current.IsOffscreen,
                boundingRect = UiaHelper.SafeRect(element.Current.BoundingRectangle),
            },
            container = new { controlType = reportedContainer.Current.ControlType?.ProgrammaticName ?? string.Empty },
        }, preserveNullFields: true);
    }

    private static void WriteScrollAmbiguousMatch(
        Selector selector, AutomationElementCollection matches, AutomationElement? scroller, double percentBefore)
    {
        var (candidates, limitMessage) = CreateCandidates(matches);
        JsonOutput.WriteError("ambiguous-match",
            $"Selector [{selector.Strategy}] '{selector.Value}' matched {matches.Count} elements.{limitMessage} Use find-all to pre-check matches and --scopeStrategy/--scopeValue to narrow the search.",
            new
            {
                count = matches.Count,
                candidates,
                verticalScrollPercentBefore = percentBefore,
                verticalScrollPercentAfter = UiaHelper.GetVerticalScrollPercent(scroller),
            });
    }

    private static void WriteNotScrollable(string reason, AutomationElement? scroller, double percentBefore)
    {
        JsonOutput.WriteError("not-scrollable",
            $"{reason} UI Automation cannot scroll it; a mouse-wheel fallback is not implemented yet.",
            new
            {
                verticalScrollPercentBefore = percentBefore,
                verticalScrollPercentAfter = UiaHelper.GetVerticalScrollPercent(scroller),
            });
    }

    private static void WriteScrollTimeout(
        Selector selector, int timeoutMs, int scrollSteps, AutomationElement container, double percentBefore)
    {
        JsonOutput.WriteError("timeout",
            $"Element [{selector.Strategy}] '{selector.Value}' was not scrolled into view within {timeoutMs}ms.",
            new
            {
                scrollSteps,
                reachedEnd = false,
                container = ToScrollContainerSummary(container),
                verticalScrollPercentBefore = percentBefore,
                verticalScrollPercentAfter = UiaHelper.GetVerticalScrollPercent(container),
            });
    }

    /// <summary>ToElementSummary for scroll-into-view errors; a window's name (its title) is emitted empty.</summary>
    private static object ToScrollContainerSummary(AutomationElement element)
    {
        var isWindow = element.Current.ControlType == ControlType.Window;
        return new
        {
            name = isWindow ? string.Empty : element.Current.Name ?? string.Empty,
            automationId = element.Current.AutomationId ?? string.Empty,
            controlType = element.Current.ControlType?.ProgrammaticName ?? string.Empty,
            className = element.Current.ClassName ?? string.Empty,
        };
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

    private static (ControlType? controlType, string? error) ParseOptionalControlType(
        Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("controlType", out var controlTypeText))
        {
            return (null, null);
        }

        return UiaHelper.TryParseControlType(controlTypeText, out var controlType)
            ? (controlType, null)
            : (null, "--controlType must be a recognized short UIA control type name, such as Button or Edit.");
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

    private static bool TryParseKeyFocusMode(string text, out UiaHelper.KeyFocusMode focusMode)
    {
        switch (text)
        {
            case "set-focus":
                focusMode = UiaHelper.KeyFocusMode.SetFocus;
                return true;
            case "click":
                focusMode = UiaHelper.KeyFocusMode.Click;
                return true;
            case "none":
                focusMode = UiaHelper.KeyFocusMode.None;
                return true;
            default:
                focusMode = default;
                return false;
        }
    }

    private static bool TryParseActionCoordinates(
        Dictionary<string, string> opts, out int x, out int y, out bool allowOutsideWindow, out string? error)
    {
        var hasSelector = opts.ContainsKey("strategy") || opts.ContainsKey("value");
        var hasCoordinates = opts.ContainsKey("x") || opts.ContainsKey("y");
        x = 0;
        y = 0;
        allowOutsideWindow = false;

        if (hasSelector == hasCoordinates)
        {
            error = "Exactly one of --strategy/--value or --x/--y is required.";
            return false;
        }

        if (!hasCoordinates)
        {
            if (opts.ContainsKey("allowOutsideWindow"))
            {
                error = "--allowOutsideWindow is only valid with the coordinate form.";
                return false;
            }

            error = null;
            return true;
        }

        if (opts.ContainsKey("scopeHwnd") || opts.ContainsKey("controlType"))
        {
            error = "--scopeHwnd and --controlType are only valid with the selector form.";
            return false;
        }

        if (!opts.TryGetValue("x", out var xText)
            || !int.TryParse(xText, NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            || !opts.TryGetValue("y", out var yText)
            || !int.TryParse(yText, NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
        {
            error = "--x and --y must both be valid integers.";
            return false;
        }

        if (opts.TryGetValue("allowOutsideWindow", out var allowOutsideText)
            && !bool.TryParse(allowOutsideText, out allowOutsideWindow))
        {
            error = "--allowOutsideWindow must be true or false (a bare flag means true).";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateCoordinateTarget(
        IntPtr hwnd, int x, int y, bool allowOutsideWindow, out string? error, bool skipIfMinimized = false)
    {
        if (allowOutsideWindow
            || (skipIfMinimized && NativeMethods.IsIconic(NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT))))
        {
            error = null;
            return true;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var bounds))
        {
            error = "Could not read the target window rectangle; coordinate input was not sent.";
            return false;
        }

        if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
        {
            error = "The coordinate point is outside the target window rectangle. Pass --allowOutsideWindow to override.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseNamedSelector(
        Dictionary<string, string> opts, string prefix, out Selector selector, out string? error)
    {
        selector = new Selector();
        error = null;

        var strategyKey = $"{prefix}Strategy";
        var valueKey = $"{prefix}Value";
        opts.TryGetValue(strategyKey, out var strategyText);
        if (!UiaHelper.TryParseImplementedSelectorStrategy(strategyText, out var strategy, out var strategyError))
        {
            error = $"--{strategyKey} is required and {strategyError}";
            return false;
        }

        if (!opts.TryGetValue(valueKey, out var value))
        {
            error = $"--{valueKey} is required.";
            return false;
        }

        selector = new Selector { Strategy = strategy, Value = value };
        return true;
    }

    /// <summary>
    /// Resolves the search scope for <see cref="FindFirst"/>/<see cref="FindAll"/>: the window
    /// root by default, or a descendant of it when <c>--scopeStrategy</c>/<c>--scopeValue</c> are
    /// both supplied (narrowing the search, e.g. to a specific panel, for both speed and to avoid
    /// ambiguous matches elsewhere in the window).
    /// </summary>
    private static (AutomationElement? scope, string? errorCode, string? error) ResolveFindScope(
        Dictionary<string, string> opts, out IntPtr diagnosticHwnd)
    {
        var (windowHwnd, errorCode, error) = ResolveWindowHwnd(opts);
        diagnosticHwnd = windowHwnd;
        if (errorCode is not null)
        {
            return (null, errorCode, error);
        }

        if (!NativeMethods.IsResponding(windowHwnd, timeoutMs: 250))
        {
            NativeMethods.EnsureWindowExists(windowHwnd);
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

    private static (AutomationElement? element, string? errorCode, string? error, object? errorDetails) ResolveElement(
        IntPtr windowHwnd, Dictionary<string, string> opts, string strategyKey = "strategy", string valueKey = "value",
        bool includeControlType = false, bool propagateWindowErrors = false,
        Func<IntPtr, AutomationElement?>? acquireWindow = null)
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
                return (null, "invalid-argument", $"Invalid scopeHwnd '{scopeHwndText}'.", null);
            }

            var (_, scopeErrorCode, scopeError) = ValidateResolvedWindowHwnd(scopeHwnd);
            if (scopeErrorCode is not null)
            {
                return (null, scopeErrorCode, scopeError, null);
            }

            var scopeElement = UiaHelper.FindWindowByHwnd(
                $"0x{scopeHwnd.ToInt64():X}", propagateWindowErrors, acquireWindow);
            return scopeElement is null
                ? (null, "element-not-found", $"No scope element found for scopeHwnd '0x{scopeHwnd.ToInt64():X}'.",
                    new { lookupDiagnostics = CreateLookupDiagnostics(scopeHwnd) })
                : ResolveElement(scopeElement, opts, strategyKey, valueKey, includeControlType, scopeHwnd);
        }

        var scope = UiaHelper.FindWindowByHwnd(
            $"0x{windowHwnd.ToInt64():X}", propagateWindowErrors, acquireWindow);
        return scope is null
            ? (null, "element-not-found", $"No window found for hwnd '0x{windowHwnd.ToInt64():X}'.",
                new { lookupDiagnostics = CreateLookupDiagnostics(windowHwnd) })
            : ResolveElement(scope, opts, strategyKey, valueKey, includeControlType, windowHwnd);
    }

    private static (AutomationElement? element, string? errorCode, string? error, object? errorDetails) ResolveElement(
        AutomationElement scope, Dictionary<string, string> opts, string strategyKey = "strategy", string valueKey = "value",
        bool includeControlType = false, IntPtr diagnosticHwnd = default)
    {
        opts.TryGetValue(strategyKey, out var strategyText);
        if (!UiaHelper.TryParseImplementedSelectorStrategy(strategyText, out var strategy, out var strategyError))
        {
            return (null, "invalid-argument",
                $"--{strategyKey} is required and {strategyError}", null);
        }

        if (!opts.TryGetValue(valueKey, out var value))
        {
            return (null, "invalid-argument", $"--{valueKey} is required.", null);
        }

        ControlType? controlType = null;
        if (includeControlType)
        {
            var parsedControlType = ParseOptionalControlType(opts);
            if (parsedControlType.error is not null)
            {
                return (null, "invalid-argument", parsedControlType.error, null);
            }

            controlType = parsedControlType.controlType;
        }

        var selector = new Selector { Strategy = strategy, Value = value };
        var matches = UiaHelper.ResolveSelectorAll(scope, selector, controlType);
        if (matches.Count == 0)
        {
            return (null, "element-not-found",
                $"No element found for selector [{strategy}] '{value}'.",
                new { lookupDiagnostics = CreateLookupDiagnostics(diagnosticHwnd) });
        }

        if (matches.Count > 1)
        {
            var (candidates, limitMessage) = CreateCandidates(matches);
            return (null, "ambiguous-match",
                $"Selector [{strategy}] '{value}' matched {matches.Count} elements.{limitMessage} Use find-all to pre-check matches and --scopeHwnd to narrow the search.",
                new { count = matches.Count, candidates });
        }

        return (matches[0], null, null, null);
    }

    private static (object[] candidates, string limitMessage) CreateCandidates(AutomationElementCollection matches)
    {
        const int candidateLimit = 10;
        var candidates = matches.Cast<AutomationElement>()
            .Take(candidateLimit)
            .Select(ToElementSummary)
            .ToArray();
        var limitMessage = matches.Count > candidateLimit
            ? $" Only the first {candidateLimit} candidates are shown."
            : string.Empty;
        return (candidates, limitMessage);
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

    internal static bool? ReadMinimizedState(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        var minimized = NativeMethods.IsIconic(hwnd);
        return NativeMethods.IsWindow(hwnd) ? minimized : null;
    }

    internal static object CreateLookupDiagnostics(IntPtr hwnd)
    {
        var root = NativeMethods.IsWindow(hwnd) ? NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) : IntPtr.Zero;
        var minimized = ReadMinimizedState(root);
        var hint = "Check the selector and search scope. If this is a WinForms tabbed UI, check the selected tab; " +
            "inactive pages may not expose their controls through UIA. No tab was switched.";
        if (minimized == true)
        {
            hint += $" The window is minimized. If interaction is intended, explicitly activate --hwnd 0x{root.ToInt64():X} --restore true, " +
                "then repeat the lookup. This diagnostic does not restore windows, activate them or switch tabs.";
        }
        return new { rootHwnd = minimized is not null ? $"0x{root.ToInt64():X}" : null, isMinimized = minimized, hint };
    }

    private static bool EnsureTargetWindowForeground(IntPtr targetHwnd, bool restoreIfMinimized = false,
        Action? onRestoreAttempt = null)
    {
        NativeMethods.EnsureWindowExists(targetHwnd);
        var targetRoot = NativeMethods.GetAncestor(targetHwnd, NativeMethods.GA_ROOT);
        if (targetRoot == IntPtr.Zero)
        {
            return false;
        }

        if (restoreIfMinimized && NativeMethods.IsIconic(targetRoot))
        {
            onRestoreAttempt?.Invoke();
            NativeMethods.ShowWindow(targetRoot, NativeMethods.SW_RESTORE);
            const int restoreTimeoutMs = 1000;
            const int restorePollMs = 20;
            var restoreTimer = Stopwatch.StartNew();
            NativeMethods.RECT? previousBounds = null;
            var restored = false;
            while (restoreTimer.ElapsedMilliseconds < restoreTimeoutMs)
            {
                if (!NativeMethods.IsIconic(targetRoot)
                    && NativeMethods.GetWindowRect(targetRoot, out var bounds))
                {
                    if (previousBounds is { } previous
                        && previous.Left == bounds.Left && previous.Top == bounds.Top
                        && previous.Right == bounds.Right && previous.Bottom == bounds.Bottom)
                    {
                        restored = true;
                        break;
                    }

                    previousBounds = bounds;
                }
                else
                {
                    previousBounds = null;
                }

                Thread.Sleep(restorePollMs);
            }

            if (!restored)
            {
                throw new ForegroundActivationException(
                    "The target window did not finish restoring within 1000 ms " +
                    "(it must be non-minimized with the same rectangle on two consecutive reads). Coordinate input was not sent.", targetRoot);
            }
        }

        if (NativeMethods.GetForegroundWindow() == targetRoot)
        {
            NativeMethods.SetExpectedForegroundWindow(targetRoot);
            return true;
        }

        if (!NativeMethods.SetForegroundWindow(targetRoot))
        {
            return false;
        }

        const int timeoutMs = 250;
        const int pollMs = 25;
        for (var elapsedMs = 0; elapsedMs < timeoutMs; elapsedMs += pollMs)
        {
            if (NativeMethods.GetForegroundWindow() == targetRoot)
            {
                NativeMethods.SetExpectedForegroundWindow(targetRoot);
                return true;
            }

            Thread.Sleep(pollMs);
        }

        var isForeground = NativeMethods.GetForegroundWindow() == targetRoot;
        if (isForeground)
        {
            NativeMethods.SetExpectedForegroundWindow(targetRoot);
        }

        return isForeground;
    }

    private static bool EnsureElementForeground(AutomationElement element, IntPtr fallbackHwnd, out IntPtr activationHwnd)
    {
        var elementHwnd = new IntPtr(element.Current.NativeWindowHandle);
        activationHwnd = elementHwnd != IntPtr.Zero ? elementHwnd : fallbackHwnd;
        return EnsureTargetWindowForeground(activationHwnd);
    }

    private static (IntPtr windowHwnd, string? errorCode, string? error) ResolveWindowHwnd(
        Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("hwnd", out var hwndText))
        {
            try
            {
                return ValidateResolvedWindowHwnd(UiaHelper.ParseHwnd(hwndText));
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
            return (IntPtr.Zero, "stale-context", $"No visible top-level windows found for pid {pid.Value}.");
        }

        var foregroundWindows = windows.Where(window => window.IsForeground).ToList();
        if (foregroundWindows.Count == 1)
        {
            return ValidateResolvedWindowHwnd(UiaHelper.ParseHwnd(foregroundWindows[0].Hwnd));
        }

        if (windows.Count == 1)
        {
            return ValidateResolvedWindowHwnd(UiaHelper.ParseHwnd(windows[0].Hwnd));
        }

        return (IntPtr.Zero, "ambiguous-window",
            $"Multiple windows found for pid {pid.Value}; pass --hwnd to select one.");
    }

    private static (IntPtr windowHwnd, string? errorCode, string? error) ValidateResolvedWindowHwnd(IntPtr hwnd)
    {
        try
        {
            NativeMethods.EnsureWindowExists(hwnd);
            if (!NativeMethods.IsResponding(hwnd, timeoutMs: 250))
            {
                NativeMethods.EnsureWindowExists(hwnd);
                return (IntPtr.Zero, "window-not-responding", "The target window exists but is not responding.");
            }

            return (hwnd, null, null);
        }
        catch (WindowGoneException ex)
        {
            return (IntPtr.Zero, "window-gone", ex.Message);
        }
    }

    private static (AutomationElement? element, string? errorCode, string? error) ResolveDiscoveredWindow(string hwnd)
    {
        var window = UiaHelper.FindWindowByHwnd(hwnd);
        return window is null
            ? (null, "element-not-found", $"No window found for hwnd '{hwnd}'.")
            : (window, null, null);
    }

    private static (UiaHelper.WindowQueryOptions? query, string? errorCode, string? error) ResolveWindowQuery(
        Dictionary<string, string> opts)
    {
        var allProcesses = IsEnabledBooleanOption(opts, "all");
        var hasPid = opts.ContainsKey("pid");
        var hasProcessName = opts.TryGetValue("processName", out var processName);
        var scopeCount = (hasPid ? 1 : 0) + (allProcesses ? 1 : 0) + (hasProcessName ? 1 : 0);
        if (scopeCount > 1)
        {
            return (null, "invalid-argument", "--pid, --all, and --processName are mutually exclusive.");
        }

        if (opts.TryGetValue("titleContains", out var titleContains) && titleContains.Length == 0)
        {
            return (null, "invalid-argument", "--titleContains must not be empty.");
        }

        if (opts.TryGetValue("className", out var className) && className.Length == 0)
        {
            return (null, "invalid-argument", "--className must not be empty.");
        }

        if (hasProcessName)
        {
            processName = processName!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^".exe".Length]
                : processName;
            if (processName.Length == 0)
            {
                return (null, "invalid-argument", "--processName must not be empty.");
            }
        }

        int? pid;
        if (allProcesses || hasProcessName)
        {
            pid = null;
        }
        else
        {
            var (resolvedPid, errorCode, error) = ResolveValidatedPid(opts);
            if (resolvedPid is null)
            {
                return (null, errorCode, error);
            }

            pid = resolvedPid.Value;
        }

        var includeUntitled = IsEnabledBooleanOption(opts, "includeUntitled");
        return (new UiaHelper.WindowQueryOptions(
            pid, allProcesses, hasProcessName ? processName : null, titleContains, className, includeUntitled), null, null);
    }

    private static bool IsEnabledBooleanOption(Dictionary<string, string> opts, string name)
    {
        return opts.TryGetValue(name, out var value) && value != "false";
    }

    private static bool TryParseWindowLimit(
        Dictionary<string, string> opts,
        out int limit,
        out string? error)
    {
        limit = 50;
        error = null;
        if (!opts.TryGetValue("limit", out var value))
        {
            return true;
        }

        if (!int.TryParse(value, out limit) || limit < 1 || limit > 200)
        {
            error = "--limit must be an integer from 1 to 200.";
            return false;
        }

        return true;
    }

    private static bool TryParseExcludedHwnds(
        Dictionary<string, string> opts,
        out HashSet<IntPtr> excludedHwnds,
        out string? error)
    {
        excludedHwnds = new HashSet<IntPtr>();
        error = null;
        if (!opts.TryGetValue("excludeHwnds", out var value))
        {
            return true;
        }

        foreach (var entry in value.Split(','))
        {
            var isHex = entry.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            var digits = isHex ? entry[2..] : entry;
            var isValidNumber = digits.Length > 0 && (isHex
                ? digits.All(Uri.IsHexDigit)
                    && ulong.TryParse(digits, System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out var hexValue)
                    && AddHwnd(hexValue, excludedHwnds)
                : digits.All(character => character is >= '0' and <= '9')
                    && ulong.TryParse(digits, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var decimalValue)
                    && AddHwnd(decimalValue, excludedHwnds));

            if (!isValidNumber)
            {
                error = "--excludeHwnds must be a comma-separated list of non-zero decimal or 0x-prefixed hexadecimal HWNDs.";
                return false;
            }
        }

        return true;
    }

    private static bool AddHwnd(ulong value, HashSet<IntPtr> excludedHwnds)
    {
        if (value == 0
            || (IntPtr.Size == sizeof(int) && value > uint.MaxValue)
            || (IntPtr.Size == sizeof(long) && value > long.MaxValue))
        {
            return false;
        }

        var hwnd = IntPtr.Size == sizeof(int)
            ? new IntPtr(unchecked((int)value))
            : new IntPtr(unchecked((long)value));
        excludedHwnds.Add(hwnd);
        return true;
    }

    private static IntPtr ParseWindowHandle(string hwnd)
    {
        var value = ulong.Parse(hwnd[2..], System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture);
        return IntPtr.Size == sizeof(int)
            ? new IntPtr(unchecked((int)value))
            : new IntPtr(unchecked((long)value));
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

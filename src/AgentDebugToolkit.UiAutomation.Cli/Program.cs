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

        var method = UiaHelper.Click(element);
        var info = UiaHelper.ToElementInfo(element, includeChildren: false, maxDepth: 0);
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
        JsonOutput.WriteSuccess(new { method });
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

using EnvDTE;
using System.Runtime.InteropServices;
using AgentDebugToolkit.Debugger.VisualStudio;

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
        case "debugger-status":
            return Verbs.DebuggerStatus(opts);
        case "get-callstack":
            return Verbs.GetCallStack(opts);
        case "get-locals":
            return Verbs.GetLocals(opts);
        case "get-exception-info":
            return Verbs.GetExceptionInfo(opts);
        case "continue":
            return Verbs.Continue(opts);
        case "step-over":
            return Verbs.StepOver(opts);
        case "step-into":
            return Verbs.StepInto(opts);
        case "step-out":
            return Verbs.StepOut(opts);
        case "start-debugging":
            return Verbs.StartDebugging(opts);
        case "stop-debugging":
            return Verbs.StopDebugging(opts);
        case "set-breakpoint":
            return Verbs.SetBreakpoint(opts);
        case "list-breakpoints":
            return Verbs.ListBreakpoints(opts);
        case "remove-breakpoint":
            return Verbs.RemoveBreakpoint(opts);
        case "wait-for-break":
            return Verbs.WaitForBreak(opts);
        default:
            JsonOutput.WriteError("invalid-argument", $"Unknown verb '{verb}'.");
            return 1;
    }
}
catch (ComBusyRetryExhaustedException ex)
{
    JsonOutput.WriteError("com-busy-retry-exhausted", ex.Message);
    return 1;
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
    public static int DebuggerStatus(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var (mode, activeDocument, activeLine) = GetStatusSnapshot(dte);
        JsonOutput.WriteSuccess(new { mode, activeDocument, activeLine }, preserveNullFields: true);
        return 0;
    }

    private static (string mode, string? activeDocument, int? activeLine) GetStatusSnapshot(DTE dte)
    {
        var mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode));

        string? activeDocument = null;
        int? activeLine = null;

        if (ComRetry.Invoke(() => dte.Debugger.CurrentMode) == dbgDebugMode.dbgBreakMode)
        {
            (activeDocument, activeLine) = TryGetLastHitLocation(dte.Debugger);
        }

        return (mode, activeDocument, activeLine);
    }

    public static int GetCallStack(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var thread = ComRetry.Invoke(() => dte.Debugger.CurrentThread);
        if (thread is null)
        {
            JsonOutput.WriteError("no-stack-frame", "The debugger has no current thread to inspect.");
            return 1;
        }

        var frames = new List<object>();
        foreach (StackFrame frame in ComRetry.Invoke(() => thread.StackFrames))
        {
            frames.Add(new { function = frame.FunctionName });
        }

        JsonOutput.WriteSuccess(new { frames });
        return 0;
    }

    public static int GetLocals(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var stackFrame = ComRetry.Invoke(() => dte.Debugger.CurrentStackFrame);
        if (stackFrame is null)
        {
            JsonOutput.WriteError("no-stack-frame", "The debugger has no current stack frame to inspect.");
            return 1;
        }

        var locals = new List<object>();
        foreach (Expression local in ComRetry.Invoke(() => stackFrame.Locals))
        {
            locals.Add(new { name = local.Name, value = local.Value, type = local.Type });
        }

        JsonOutput.WriteSuccess(new { locals });
        return 0;
    }

    public static int GetExceptionInfo(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var stackFrame = ComRetry.Invoke(() => dte.Debugger.CurrentStackFrame);
        if (stackFrame is null)
        {
            JsonOutput.WriteError("no-stack-frame", "The debugger has no current stack frame to inspect.");
            return 1;
        }

        Expression? exceptionExpr = null;
        foreach (Expression local in ComRetry.Invoke(() => stackFrame.Locals))
        {
            if (local.Name == "$exception")
            {
                exceptionExpr = local;
                break;
            }
        }

        if (exceptionExpr is null)
        {
            JsonOutput.WriteError("no-active-exception", "Break mode was not triggered by an exception.");
            return 1;
        }

        JsonOutput.WriteSuccess(new { exceptionType = exceptionExpr.Type, message = exceptionExpr.Value });
        return 0;
    }

    public static int Continue(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (ComRetry.Invoke(() => dte.Debugger.CurrentMode) == dbgDebugMode.dbgDesignMode)
        {
            // EnvDTE's Debugger.Go is the same entry point as Start Debugging (F5): with no
            // active debug session it launches a brand-new one against the IDE's current
            // startup project, rather than being a harmless no-op. Guard against that surprising
            // side effect instead of silently starting a new session on the caller's behalf.
            JsonOutput.WriteError(
                "no-active-session", "There is no active debugging session to resume.");
            return 1;
        }

        ComRetry.Invoke(() => dte.Debugger.Go(WaitForBreakOrEnd: false));
        JsonOutput.WriteSuccess(new { mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode)) });
        return 0;
    }

    public static int StepOver(Dictionary<string, string> opts) => Step(opts, d => d.StepOver(false));

    public static int StepInto(Dictionary<string, string> opts) => Step(opts, d => d.StepInto(false));

    public static int StepOut(Dictionary<string, string> opts) => Step(opts, d => d.StepOut(false));

    private static int Step(Dictionary<string, string> opts, Action<Debugger> stepAction)
    {
        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (ComRetry.Invoke(() => dte.Debugger.CurrentStackFrame) is null)
        {
            JsonOutput.WriteError("no-stack-frame", "The debugger has no current stack frame to step from.");
            return 1;
        }

        ComRetry.Invoke(() => stepAction(dte.Debugger));
        JsonOutput.WriteSuccess(new { mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode)) });
        return 0;
    }

    public static int StartDebugging(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (ComRetry.Invoke(() => dte.Debugger.CurrentMode) != dbgDebugMode.dbgDesignMode)
        {
            JsonOutput.WriteError(
                "already-debugging", "A debugging session is already active (run or break mode).");
            return 1;
        }

        // ExecuteCommand("Debug.Start") is the documented, IDE-menu-equivalent way to trigger
        // F5 (start the configured startup project) via EnvDTE; Debugger.Go with no active
        // session has the same effect but going through the command keeps this verb's intent
        // explicit and consistent with how VS itself exposes "Start Debugging".
        ComRetry.Invoke(() => dte.ExecuteCommand("Debug.Start", ""));
        JsonOutput.WriteSuccess(new { mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode)) });
        return 0;
    }

    public static int StopDebugging(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        if (ComRetry.Invoke(() => dte.Debugger.CurrentMode) == dbgDebugMode.dbgDesignMode)
        {
            JsonOutput.WriteError("no-active-session", "There is no active debugging session to stop.");
            return 1;
        }

        ComRetry.Invoke(() => dte.Debugger.Stop(WaitForDesignMode: true));
        JsonOutput.WriteSuccess(new { mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode)) });
        return 0;
    }

    public static int SetBreakpoint(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("file", out var file) || string.IsNullOrWhiteSpace(file))
        {
            JsonOutput.WriteError("invalid-argument", "--file is required.");
            return 1;
        }

        if (!opts.TryGetValue("line", out var lineText) || !int.TryParse(lineText, out var line) || line <= 0)
        {
            JsonOutput.WriteError("invalid-argument", "--line is required and must be a positive integer.");
            return 1;
        }

        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var added = ComRetry.Invoke(() => dte.Debugger.Breakpoints.Add(File: file, Line: line));
        try
        {
            var breakpoint = added.Item(1);
            try
            {
                JsonOutput.WriteSuccess(new { file = breakpoint.File, line = breakpoint.FileLine, enabled = breakpoint.Enabled });
            }
            finally
            {
                Marshal.ReleaseComObject(breakpoint);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(added);
        }

        return 0;
    }

    public static int ListBreakpoints(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var breakpoints = new List<object>();
        var breakpointsCollection = ComRetry.Invoke(() => dte.Debugger.Breakpoints);
        try
        {
            foreach (Breakpoint breakpoint in breakpointsCollection)
            {
                try
                {
                    breakpoints.Add(new { file = breakpoint.File, line = breakpoint.FileLine, enabled = breakpoint.Enabled });
                }
                finally
                {
                    Marshal.ReleaseComObject(breakpoint);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(breakpointsCollection);
        }

        JsonOutput.WriteSuccess(new { breakpoints });
        return 0;
    }

    public static int RemoveBreakpoint(Dictionary<string, string> opts)
    {
        var all = opts.TryGetValue("all", out var allText) && allText == "true";
        opts.TryGetValue("file", out var file);
        opts.TryGetValue("line", out var lineText);

        if (all && (file is not null || lineText is not null))
        {
            JsonOutput.WriteError("invalid-argument", "--all cannot be combined with --file/--line.");
            return 1;
        }

        int? line = null;
        if (!all)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                JsonOutput.WriteError("invalid-argument", "--file is required unless --all is specified.");
                return 1;
            }

            if (lineText is null || !int.TryParse(lineText, out var parsedLine) || parsedLine <= 0)
            {
                JsonOutput.WriteError("invalid-argument", "--line is required and must be a positive integer unless --all is specified.");
                return 1;
            }

            line = parsedLine;
        }

        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var removed = 0;
        // Delete() while iterating: snapshot to a list first so removing an item doesn't disturb
        // the live COM collection's enumeration (EnvDTE.Breakpoints has no documented guarantee
        // that Delete() during foreach is safe).
        var candidates = new List<Breakpoint>();
        var breakpointsCollection = ComRetry.Invoke(() => dte.Debugger.Breakpoints);
        try
        {
            foreach (Breakpoint breakpoint in breakpointsCollection)
            {
                candidates.Add(breakpoint);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(breakpointsCollection);
        }

        foreach (var breakpoint in candidates)
        {
            try
            {
                if (all || (string.Equals(breakpoint.File, file, StringComparison.OrdinalIgnoreCase) && breakpoint.FileLine == line))
                {
                    ComRetry.Invoke(() => breakpoint.Delete());
                    removed++;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(breakpoint);
            }
        }

        if (!all && removed == 0)
        {
            JsonOutput.WriteError("breakpoint-not-found", $"No breakpoint found at {file}:{line}.");
            return 1;
        }

        JsonOutput.WriteSuccess(new { removed });
        return 0;
    }

    public static int WaitForBreak(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("timeoutMs", out var timeoutText) || !int.TryParse(timeoutText, out var timeoutMs) || timeoutMs < 0)
        {
            JsonOutput.WriteError("invalid-argument", "--timeoutMs is required and must be a non-negative integer.");
            return 1;
        }

        var pollMs = 250;
        if (opts.TryGetValue("pollMs", out var pollText))
        {
            if (!int.TryParse(pollText, out pollMs) || pollMs <= 0)
            {
                JsonOutput.WriteError("invalid-argument", "--pollMs must be a positive integer.");
                return 1;
            }
        }

        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // No local catch for ComBusyRetryExhaustedException here: this loop runs entirely inside
        // the top-level try/catch in Main, so if GetStatusSnapshot's ComRetry.Invoke calls
        // exhaust their retries mid-poll, that exception already propagates up to Main's
        // existing handler and is reported as "com-busy-retry-exhausted" rather than
        // "unhandled-exception".
        while (true)
        {
            var (mode, activeDocument, activeLine) = GetStatusSnapshot(dte);
            if (mode == "break")
            {
                JsonOutput.WriteSuccess(new { mode, activeDocument, activeLine }, preserveNullFields: true);
                return 0;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                JsonOutput.WriteError("timeout", $"Break mode was not reached within {timeoutMs}ms.");
                return 1;
            }

            System.Threading.Thread.Sleep(Math.Min(pollMs, (int)Math.Max(0, timeoutMs - stopwatch.ElapsedMilliseconds)));
        }
    }

    private static (DTE? dte, string? errorCode, string? error) ResolveDte(Dictionary<string, string> opts)
    {
        opts.TryGetValue("solution", out var solutionName);
        return DteLocator.FindDte(solutionName);
    }

    private static (DTE? dte, string? errorCode, string? error) RequireBreakMode(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = ResolveDte(opts);
        if (dte is null)
        {
            return (null, errorCode, error);
        }

        if (ComRetry.Invoke(() => dte.Debugger.CurrentMode) != dbgDebugMode.dbgBreakMode)
        {
            return (null, "not-in-break-mode", "The debugger is not currently in break mode.");
        }

        return (dte, null, null);
    }

    private static string ToModeString(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgDesignMode => "design",
        dbgDebugMode.dbgRunMode => "run",
        dbgDebugMode.dbgBreakMode => "break",
        _ => "unknown"
    };

    private static (string? file, int? line) TryGetLastHitLocation(Debugger debugger)
    {
        try
        {
            var breakpoint = ComRetry.Invoke(() => debugger.BreakpointLastHit);
            return breakpoint is null ? (null, null) : (breakpoint.File, breakpoint.FileLine);
        }
        catch
        {
            // No breakpoint object is available when break mode was triggered by something
            // other than a hit breakpoint (e.g. an unhandled exception, a "break all"); this is
            // expected, not an error condition for debugger-status.
            return (null, null);
        }
    }
}

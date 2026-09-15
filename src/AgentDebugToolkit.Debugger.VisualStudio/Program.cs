using EnvDTE;
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

        var mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode));

        string? activeDocument = null;
        int? activeLine = null;

        if (ComRetry.Invoke(() => dte.Debugger.CurrentMode) == dbgDebugMode.dbgBreakMode)
        {
            (activeDocument, activeLine) = TryGetLastHitLocation(dte.Debugger);
        }

        JsonOutput.WriteSuccess(new { mode, activeDocument, activeLine }, preserveNullFields: true);
        return 0;
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

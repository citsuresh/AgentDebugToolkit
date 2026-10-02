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
        case "attach-process":
            return Verbs.AttachProcess(opts);
        case "break-all":
            return Verbs.BreakAll(opts);
        case "detach":
            return Verbs.Detach(opts);
        case "list-threads":
            return Verbs.ListThreads(opts);
        case "select-thread":
            return Verbs.SelectThread(opts);
        case "select-frame":
            return Verbs.SelectFrame(opts);
        case "evaluate":
            return Verbs.Evaluate(opts);
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
            var debugger = ComRetry.Invoke(() => dte.Debugger);
            (activeDocument, activeLine) = TryGetLastHitLocation(debugger);
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
        ComRetry.ForEach<StackFrame>(ComRetry.Invoke(() => thread.StackFrames), frame =>
        {
            frames.Add(new { function = ComRetry.Invoke(() => frame.FunctionName) });
        });

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
        ComRetry.ForEach<Expression>(ComRetry.Invoke(() => stackFrame.Locals), local =>
        {
            locals.Add(new
            {
                name = ComRetry.Invoke(() => local.Name),
                value = ComRetry.Invoke(() => local.Value),
                type = ComRetry.Invoke(() => local.Type),
            });
        });

        JsonOutput.WriteSuccess(new { locals });
        return 0;
    }

    public static int ListThreads(Dictionary<string, string> opts)
    {
        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var program = ComRetry.Invoke(() => dte.Debugger.CurrentProgram);
        if (program is null)
        {
            JsonOutput.WriteError("no-current-program", "The debugger has no current program (debuggee) to enumerate threads from.");
            return 1;
        }

        try
        {
            var threads = new List<object>();
            var threadsCollection = ComRetry.Invoke(() => program.Threads);
            try
            {
                ComRetry.ForEach<EnvDTE.Thread>(threadsCollection, thread =>
                {
                    try
                    {
                        var id = ComRetry.Invoke(() => thread.ID);
                        var name = ComRetry.Invoke(() => thread.Name);
                        threads.Add(new { id, name });
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(thread);
                    }
                });
            }
            finally
            {
                Marshal.ReleaseComObject(threadsCollection);
            }

            JsonOutput.WriteSuccess(new { threads });
            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(program);
        }
    }

    public static int SelectThread(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("threadId", out var threadIdText) || !int.TryParse(threadIdText, out var threadId))
        {
            JsonOutput.WriteError("invalid-argument", "--threadId is required and must be an integer.");
            return 1;
        }

        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var (thread, selectError) = ResolveThreadById(dte, threadId);
        if (thread is null)
        {
            JsonOutput.WriteError(selectError!.Value.code, selectError.Value.message);
            return 1;
        }

        try
        {
            // Debugger.CurrentThread is documented as a settable property (not just a read-only
            // reflection of whatever EnvDTE/the IDE's UI last focused) -- this call actually
            // changes which thread subsequent get-locals/get-callstack/evaluate calls operate on.
            ComRetry.Invoke(() => dte.Debugger.CurrentThread = thread);

            // Cache ID/Name once via ComRetry: every later reference to them re-uses this
            // already-retried value rather than re-reading bare COM properties, which could
            // surface a transient busy HRESULT as an unwrapped COMException.
            var threadId2 = ComRetry.Invoke(() => thread.ID);
            var threadName = ComRetry.Invoke(() => thread.Name);

            // Re-read CurrentThread immediately after the set rather than trusting the assignment
            // blindly: EnvDTE's property setter does its own COM marshaling/AddRef on the object
            // passed in, so releasing our own RCW for `thread` right after the set (see finally
            // below) is safe -- but if VS's internal state didn't actually adopt the selection
            // (e.g. a stale/mismatched thread reference, or an internal state transition raced
            // the call), silently reporting success here would let a caller trust a selection
            // that never took effect. Fail loudly instead of guessing.
            var confirmed = ComRetry.Invoke(() => dte.Debugger.CurrentThread);
            try
            {
                var confirmedId = confirmed is null ? (int?)null : ComRetry.Invoke(() => confirmed.ID);
                if (confirmed is null || confirmedId != threadId2)
                {
                    JsonOutput.WriteError(
                        "thread-selection-not-applied",
                        $"select-thread set Debugger.CurrentThread to thread {threadId2}, but reading it back afterward did not confirm the selection (got {(confirmedId?.ToString() ?? "null")}).");
                    return 1;
                }

                JsonOutput.WriteSuccess(new { threadId = threadId2, name = threadName });
                return 0;
            }
            finally
            {
                if (confirmed is not null)
                {
                    Marshal.ReleaseComObject(confirmed);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(thread);
        }
    }

    public static int SelectFrame(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("index", out var indexText) || !int.TryParse(indexText, out var index) || index < 0)
        {
            JsonOutput.WriteError("invalid-argument", "--index is required and must be a non-negative integer.");
            return 1;
        }

        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var thread = ComRetry.Invoke(() => dte.Debugger.CurrentThread);
        if (thread is null)
        {
            JsonOutput.WriteError("no-thread-selected", "No current thread is selected; call select-thread (or ensure the debugger has a current thread) first.");
            return 1;
        }

        try
        {
            var framesCollection = ComRetry.Invoke(() => thread.StackFrames);
            try
            {
                // StackFrame has no stable id/handle of its own in this interop surface -- unlike
                // Thread.ID, frames are only addressable by their position in the live
                // StackFrames collection, so --index is resolved fresh against the collection on
                // every call rather than against any cached frame reference.
                var position = 1;
                (string code, string message)? resultError = null;
                object? resultSuccess = null;
                var matched = false;

                ComRetry.ForEach<StackFrame>(framesCollection, candidate =>
                {
                    if (matched)
                    {
                        Marshal.ReleaseComObject(candidate);
                        return true;
                    }

                    if (position - 1 == index)
                    {
                        matched = true;
                        try
                        {
                            // Cache FunctionName once via ComRetry up front: every later reference to
                            // it in this block is this already-retried value, so a transient busy
                            // HRESULT here is retried/surfaced consistently via ComRetry rather than
                            // leaking out as an unwrapped COMException from a bare property read.
                            var candidateFunctionName = ComRetry.Invoke(() => candidate.FunctionName);

                            if (!IsManagedFrame(candidate, candidateFunctionName))
                            {
                                resultError = (
                                    "frame-not-managed",
                                    $"Frame {index} ('{candidateFunctionName}') is not a managed/evaluable frame " +
                                    "(e.g. a native transition or a 'paused execution' placeholder) -- select a " +
                                    "different frame index.");
                                return true;
                            }

                            ComRetry.Invoke(() => dte.Debugger.CurrentStackFrame = candidate);

                            // Re-read CurrentStackFrame immediately after the set, the same way
                            // SelectThread verifies CurrentThread above: StackFrame has no stable
                            // id to compare, so function name + a presence check is the best
                            // available confirmation that the assignment actually took effect
                            // rather than being silently dropped/ignored by VS's internal state.
                            var confirmedFrame = ComRetry.Invoke(() => dte.Debugger.CurrentStackFrame);
                            try
                            {
                                if (confirmedFrame is null)
                                {
                                    resultError = (
                                        "frame-selection-not-applied",
                                        $"select-frame set Debugger.CurrentStackFrame to frame {index} ('{candidateFunctionName}'), " +
                                        "but reading it back afterward returned no current stack frame.");
                                    return true;
                                }

                                resultSuccess = new { index, function = candidateFunctionName };
                                return true;
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(confirmedFrame);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(candidate);
                        }
                    }

                    Marshal.ReleaseComObject(candidate);
                    position++;
                    return true;
                });

                if (resultError is not null)
                {
                    JsonOutput.WriteError(resultError.Value.code, resultError.Value.message);
                    return 1;
                }

                if (resultSuccess is not null)
                {
                    JsonOutput.WriteSuccess(resultSuccess);
                    return 0;
                }

                var threadId = ComRetry.Invoke(() => thread.ID);
                JsonOutput.WriteError("frame-index-out-of-range", $"Thread {threadId} has {position - 1} stack frame(s); index {index} is out of range.");
                return 1;
            }
            finally
            {
                Marshal.ReleaseComObject(framesCollection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(thread);
        }
    }

    // EnvDTE's StackFrame exposes no explicit IsManaged flag; Language is the reliable signal for
    // whether this frame can actually be evaluated (GetExpression/ExecuteStatement). Native
    // transition frames and VS's own "[[Application execution paused...]]" / "[Managed to Native
    // Transition]" pseudo-frames either throw an empty/absent Language or a raw HRESULT
    // (0x89711006, "no symbols"/"not evaluable" in the native debug engine) when FunctionName or
    // Language is read, rather than returning a normal managed language name like "C#" -- treat
    // any frame whose FunctionName looks like one of these known placeholders, or whose Language
    // read fails/comes back empty, as non-managed/non-evaluable.
    private static bool IsManagedFrame(StackFrame frame, string functionName)
    {
        if (string.IsNullOrEmpty(functionName)
            || functionName.Contains("Native Transition", StringComparison.OrdinalIgnoreCase)
            || functionName.Contains("execution paused", StringComparison.OrdinalIgnoreCase)
            || functionName.StartsWith('[') && functionName.EndsWith(']'))
        {
            return false;
        }

        try
        {
            var language = ComRetry.Invoke(() => frame.Language);
            return !string.IsNullOrEmpty(language);
        }
        catch (COMException)
        {
            return false;
        }
    }

    public static int Evaluate(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("expression", out var expressionText) || string.IsNullOrWhiteSpace(expressionText))
        {
            JsonOutput.WriteError("invalid-argument", "--expression is required.");
            return 1;
        }

        var allowSideEffects = opts.TryGetValue("allowSideEffects", out var allowText)
            && string.Equals(allowText, "true", StringComparison.OrdinalIgnoreCase);

        var (dte, errorCode, error) = RequireBreakMode(opts);
        if (dte is null)
        {
            JsonOutput.WriteError(errorCode!, error!);
            return 1;
        }

        var stackFrame = ComRetry.Invoke(() => dte.Debugger.CurrentStackFrame);
        if (stackFrame is null)
        {
            JsonOutput.WriteError(
                "no-frame-selected",
                "No managed stack frame is currently selected to evaluate against -- select a thread/frame " +
                "(select-thread/select-frame) first, or confirm the debugger has a current managed stack frame " +
                "(it may be stale, or the attached process may have no managed frames).");
            return 1;
        }

        try
        {
            // Cache FunctionName once via ComRetry: every later reference to it in this method
            // re-uses this already-retried value rather than re-reading the bare COM property
            // (which could surface a transient busy HRESULT as an unwrapped COMException instead
            // of being retried/reported consistently via ComRetry/com-busy-retry-exhausted).
            var stackFrameFunctionName = ComRetry.Invoke(() => stackFrame.FunctionName);

            if (!IsManagedFrame(stackFrame, stackFrameFunctionName))
            {
                JsonOutput.WriteError(
                    "frame-not-managed",
                    $"The current stack frame ('{stackFrameFunctionName}') is not a managed/evaluable frame " +
                    "(e.g. a native transition or a 'paused execution' placeholder) -- select a managed frame " +
                    "(select-frame) before evaluating.");
                return 1;
            }

            if (allowSideEffects)
            {
                // UNSAFE opt-in path: ExecuteStatement can run property getters, method calls, and
                // assignment statements against the live debuggee. Only reached when the caller
                // explicitly passes --allowSideEffects=true.
                try
                {
                    ComRetry.Invoke(() => dte.Debugger.ExecuteStatement(expressionText, Timeout: -1, TreatAsExpression: true));
                }
                catch (COMException ex)
                {
                    JsonOutput.WriteError(
                        "evaluation-failed",
                        $"Expression '{expressionText}' could not be executed in the current frame: {DescribeComFailure(ex)}");
                    return 1;
                }

                JsonOutput.WriteSuccess(new { expression = expressionText, allowSideEffects = true, executed = true });
                return 0;
            }

            // Safe-by-default path: Debugger.GetExpression evaluates (does not execute) an expression
            // against the current stack frame -- the same read mechanism get-locals already uses for
            // Locals entries -- and never runs assignment statements. Implicit property-getter/
            // function evaluation during this read is governed solely by Visual Studio's own global
            // Tools > Options > Debugging > General "Allow property evaluation and other implicit
            // function calls" setting, not by anything this CLI can toggle per-call; this verb does
            // not change that setting and does not attempt to call methods/assignments itself.
            Expression? result;
            try
            {
                result = ComRetry.Invoke(() => dte.Debugger.GetExpression(expressionText, UseAutoExpandRules: false, Timeout: -1));
            }
            catch (COMException ex)
            {
                // Any transient-busy HRESULT is already retried/surfaced by ComRetry as
                // com-busy-retry-exhausted before reaching here; this catch is for everything
                // else the native debug engine can throw synchronously for an unevaluable
                // expression/frame combination (e.g. 0x89711006), so the caller gets a specific,
                // actionable message instead of the generic top-level unhandled-exception.
                JsonOutput.WriteError(
                    "evaluation-failed",
                    $"Expression '{expressionText}' could not be evaluated in the current frame: {DescribeComFailure(ex)}");
                return 1;
            }

            if (result is null || !result.IsValidValue)
            {
                JsonOutput.WriteError(
                    "evaluation-failed",
                    $"Expression '{expressionText}' could not be evaluated in the current frame.",
                    new { name = result?.Name, type = result?.Type });
                return 1;
            }

            JsonOutput.WriteSuccess(new { name = result.Name, value = result.Value, type = result.Type });
            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(stackFrame);
        }
    }

    // Translates a COMException surfaced while evaluating/executing an expression into a short,
    // human-readable description instead of a raw HRESULT. 0x89711006 is the native debug
    // engine's "expression could not be evaluated in this context" HRESULT (observed live when
    // attempting to evaluate against native/non-evaluable transition frames); anything else is
    // reported with its HRESULT for diagnosability.
    private static string DescribeComFailure(COMException ex) => unchecked((uint)ex.ErrorCode) switch
    {
        0x89711006 => "the native debug engine reported this expression cannot be evaluated in the current context " +
                      "(commonly seen for native/non-managed frames or during a native-to-managed transition).",
        _ => $"{ex.Message} (0x{unchecked((uint)ex.ErrorCode):X8})"
    };

    private static (EnvDTE.Thread? thread, (string code, string message)? error) ResolveThreadById(DTE dte, int threadId)
    {
        var program = ComRetry.Invoke(() => dte.Debugger.CurrentProgram);
        if (program is null)
        {
            return (null, ("no-current-program", "The debugger has no current program (debuggee) to select a thread from."));
        }

        try
        {
            var threadsCollection = ComRetry.Invoke(() => program.Threads);
            try
            {
                EnvDTE.Thread? found = null;
                ComRetry.ForEach<EnvDTE.Thread>(threadsCollection, candidate =>
                {
                    if (found is null && ComRetry.Invoke(() => candidate.ID) == threadId)
                    {
                        found = candidate;
                        return false;
                    }

                    Marshal.ReleaseComObject(candidate);
                    return true;
                });

                return found is not null
                    ? (found, null)
                    : (null, ("thread-not-found", $"No thread with id {threadId} was found in the current program."));
            }
            finally
            {
                Marshal.ReleaseComObject(threadsCollection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(program);
        }
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
        ComRetry.ForEach<Expression>(ComRetry.Invoke(() => stackFrame.Locals), local =>
        {
            if (ComRetry.Invoke(() => local.Name) == "$exception")
            {
                exceptionExpr = local;
                return false;
            }
            return true;
        });

        if (exceptionExpr is null)
        {
            JsonOutput.WriteError("no-active-exception", "Break mode was not triggered by an exception.");
            return 1;
        }

        JsonOutput.WriteSuccess(new
        {
            exceptionType = ComRetry.Invoke(() => exceptionExpr.Type),
            message = ComRetry.Invoke(() => exceptionExpr.Value),
        });
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
            ComRetry.ForEach<Breakpoint>(breakpointsCollection, breakpoint =>
            {
                try
                {
                    breakpoints.Add(new
                    {
                        file = ComRetry.Invoke(() => breakpoint.File),
                        line = ComRetry.Invoke(() => breakpoint.FileLine),
                        enabled = ComRetry.Invoke(() => breakpoint.Enabled),
                    });
                }
                finally
                {
                    Marshal.ReleaseComObject(breakpoint);
                }
            });
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
            ComRetry.ForEach<Breakpoint>(breakpointsCollection, breakpoint => candidates.Add(breakpoint));
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

    public static int AttachProcess(Dictionary<string, string> opts)
    {
        if (!TryParsePid(opts, out var pid))
        {
            return 1;
        }

        opts.TryGetValue("solution", out var solutionName);
        var (dte, process, errorCode, error, candidates) = DteLocator.FindProcessToAttach(pid, solutionName);
        if (dte is null || process is null)
        {
            JsonOutput.WriteError(errorCode!, error!, candidates is null ? null : new { candidates });
            return 1;
        }

        try
        {
            // EnvDTE.Process.Attach() is the only supported way to start debugging an
            // already-running process (no UI automation / SendKeys involved): it is the same
            // COM entry point the IDE's own "Attach to Process" dialog calls internally.
            ComRetry.Invoke(() => process.Attach());
            JsonOutput.WriteSuccess(new { pid, mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode)) });
            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(process);
        }
    }

    public static int BreakAll(Dictionary<string, string> opts)
    {
        if (!TryParsePid(opts, out var pid))
        {
            return 1;
        }

        opts.TryGetValue("solution", out var solutionName);
        var (dte, process, errorCode, error, candidates) = DteLocator.FindDebuggedProcess(pid, solutionName);
        if (dte is null || process is null)
        {
            JsonOutput.WriteError(errorCode!, error!, candidates is null ? null : new { candidates });
            return 1;
        }

        try
        {
            // Process.Break(WaitForBreakMode) breaks every thread in this specific debuggee
            // process (not just the current thread, and not every process VS is debugging),
            // matching EnvDTE's documented remarks for this overload. WaitForBreakMode: true so
            // the mode reported back reflects the break having actually taken effect.
            ComRetry.Invoke(() => process.Break(WaitForBreakMode: true));
            JsonOutput.WriteSuccess(new { pid, mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode)) });
            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(process);
        }
    }

    public static int Detach(Dictionary<string, string> opts)
    {
        if (!TryParsePid(opts, out var pid))
        {
            return 1;
        }

        opts.TryGetValue("solution", out var solutionName);
        var (dte, process, errorCode, error, candidates) = DteLocator.FindDebuggedProcess(pid, solutionName);
        if (dte is null || process is null)
        {
            JsonOutput.WriteError(errorCode!, error!, candidates is null ? null : new { candidates });
            return 1;
        }

        try
        {
            // Process.Detach(WaitForBreakOrEnd) stops the debugger from monitoring this process
            // without terminating it -- distinct from Debugger.Stop(), which ends the debuggee.
            // WaitForBreakOrEnd: false so this call returns immediately rather than blocking
            // until the (now undebugged) process happens to hit a break or exit on its own.
            ComRetry.Invoke(() => process.Detach(WaitForBreakOrEnd: false));
            JsonOutput.WriteSuccess(new { pid, mode = ToModeString(ComRetry.Invoke(() => dte.Debugger.CurrentMode)) });
            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(process);
        }
    }

    private static bool TryParsePid(Dictionary<string, string> opts, out int pid)
    {
        if (!opts.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, out pid) || pid <= 0)
        {
            JsonOutput.WriteError("invalid-argument", "--pid is required and must be a positive integer.");
            pid = 0;
            return false;
        }

        return true;
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

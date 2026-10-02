using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AgentDebugToolkit.Mcp;

/// <summary>
/// One MCP tool per agentdebug-vs.exe verb, as documented in docs/CLI_CONTRACT.md's
/// "CLI Contract — AgentDebugToolkit.Debugger.VisualStudio" section. Each tool only builds an
/// argument list and relays the CLI's own JSON output — no EnvDTE/COM logic lives here.
/// </summary>
[McpServerToolType]
public sealed class VsDebuggerTools
{
    private const string Exe = "agentdebug-vs.exe";
    private readonly CliRunner _cli;

    public VsDebuggerTools(CliRunner cli)
    {
        _cli = cli;
    }

    private static List<string> BuildArgs(string verb, string? solution, params (string Flag, string? Value)[] options)
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
        if (!string.IsNullOrWhiteSpace(solution))
        {
            args.Add("--solution");
            args.Add(solution);
        }
        return args;
    }

    private Task<ToolInvocationResult> RunAsync(List<string> args, int? timeoutMs, CancellationToken ct) =>
        RunInternalAsync(args, timeoutMs, ct);

    private async Task<ToolInvocationResult> RunInternalAsync(List<string> args, int? timeoutMs, CancellationToken ct)
    {
        var result = await _cli.RunAsync(Exe, args, timeoutMs, ct);
        return CliRunner.ToInvocationResult(result);
    }

    [McpServerTool(Name = "vs_debugger_status", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Reports the Visual Studio debugger's current mode (design/run/break) and, if stopped at a breakpoint, the last-hit file/line. Read-only.")]
    public Task<ToolInvocationResult> DebuggerStatus(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("debugger-status", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_get_callstack", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns the current thread's call stack (innermost frame first). Requires the debugger to be in break mode. Read-only.")]
    public Task<ToolInvocationResult> GetCallStack(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("get-callstack", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_get_locals", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns local variables for the current (innermost) stack frame, or the frame selected via vs_select_frame. Requires break mode. Read-only.")]
    public Task<ToolInvocationResult> GetLocals(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("get-locals", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_get_exception_info", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns exception details if break mode was triggered by a thrown exception. Requires break mode. Read-only.")]
    public Task<ToolInvocationResult> GetExceptionInfo(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("get-exception-info", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_continue", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Resumes execution of the active debugging session (Debugger.Go). Requires an active session; does not start a new one. Changes debuggee state.")]
    public Task<ToolInvocationResult> Continue(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("continue", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_step_over", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Steps over one line (Debugger.StepOver). Requires break mode. Changes debuggee state.")]
    public Task<ToolInvocationResult> StepOver(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("step-over", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_step_into", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Steps into one line (Debugger.StepInto). Requires break mode. Changes debuggee state.")]
    public Task<ToolInvocationResult> StepInto(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("step-into", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_step_out", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Steps out of the current function (Debugger.StepOut). Requires break mode. Changes debuggee state.")]
    public Task<ToolInvocationResult> StepOut(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("step-out", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_start_debugging", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Starts a new debugging session (F5 / Debug.Start) for the IDE's configured startup project. Only valid from design mode. Risk: launches a new process.")]
    public Task<ToolInvocationResult> StartDebugging(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("start-debugging", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_stop_debugging", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Stops the active debugging session (Debugger.Stop), terminating the debuggee. Risk: this ends the debuggee process, unlike vs_detach.")]
    public Task<ToolInvocationResult> StopDebugging(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("stop-debugging", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_set_breakpoint", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Adds a breakpoint at the given file/line. Changes debugger state.")]
    public Task<ToolInvocationResult> SetBreakpoint(
        [Description("Absolute source file path.")] string file,
        [Description("1-based line number.")] int line,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("set-breakpoint", solution, ("--file", file), ("--line", line.ToString())), null, cancellationToken);

    [McpServerTool(Name = "vs_list_breakpoints", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Enumerates all currently set breakpoints in the attached Visual Studio instance. Read-only.")]
    public Task<ToolInvocationResult> ListBreakpoints(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("list-breakpoints", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_remove_breakpoint", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Removes one breakpoint (file+line) or every breakpoint (all=true). Changes debugger state.")]
    public Task<ToolInvocationResult> RemoveBreakpoint(
        [Description("Absolute source file path. Omit when all=true.")] string? file = null,
        [Description("1-based line number. Omit when all=true.")] int? line = null,
        [Description("Remove every breakpoint instead of one specific file/line. Mutually exclusive with file/line.")] bool all = false,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs(
            "remove-breakpoint",
            solution,
            ("--file", file),
            ("--line", line?.ToString()),
            ("--all", all ? "true" : null)), null, cancellationToken);

    [McpServerTool(Name = "vs_wait_for_break", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Polls until the debugger enters break mode or the timeout elapses. Read-only (observational), but blocks for up to timeoutMs.")]
    public Task<ToolInvocationResult> WaitForBreak(
        [Description("Maximum time to wait, in milliseconds. Capped at 120000 (2 minutes); the tool's own process-level timeout allows up to 10s extra beyond this to let the child CLI flush its final output.")] int timeoutMs,
        [Description("Poll interval in milliseconds (default 250).")] int? pollMs = null,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
    {
        const int MaxWaitForBreakMs = 120_000;
        var cappedTimeoutMs = Math.Clamp(timeoutMs, 0, MaxWaitForBreakMs);
        var args = BuildArgs("wait-for-break", solution, ("--timeoutMs", cappedTimeoutMs.ToString()), ("--pollMs", pollMs?.ToString()));
        // The child CLI's own poll loop already bounds itself by --timeoutMs; give the process
        // timeout a little headroom above that so a legitimate full-length wait isn't killed early.
        return RunAsync(args, cappedTimeoutMs + 10_000, cancellationToken);
    }

    [McpServerTool(Name = "vs_attach_process", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Attaches the Visual Studio debugger to an already-running process by PID. Changes debugger state; fails with ambiguous-process/process-not-found rather than guessing if the target is unclear.")]
    public Task<ToolInvocationResult> AttachProcess(
        [Description("PID of the target debuggee process.")] int pid,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("attach-process", solution, ("--pid", pid.ToString())), null, cancellationToken);

    [McpServerTool(Name = "vs_break_all", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Breaks all threads in the target debuggee process (not just the current thread). Changes debugger state.")]
    public Task<ToolInvocationResult> BreakAll(
        [Description("PID of the target debuggee process (must already be under an active debug session).")] int pid,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("break-all", solution, ("--pid", pid.ToString())), null, cancellationToken);

    [McpServerTool(Name = "vs_detach", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Detaches the debugger from the target debuggee process without terminating it (Process.Detach, not Debugger.Stop). The debuggee keeps running afterward.")]
    public Task<ToolInvocationResult> Detach(
        [Description("PID of the target debuggee process (must already be under an active debug session).")] int pid,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("detach", solution, ("--pid", pid.ToString())), null, cancellationToken);

    [McpServerTool(Name = "vs_list_threads", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Enumerates the currently-debugged program's threads (id + name). Requires break mode. Read-only.")]
    public Task<ToolInvocationResult> ListThreads(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("list-threads", solution), null, cancellationToken);

    [McpServerTool(Name = "vs_select_thread", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Selects a thread (by the id returned from vs_list_threads) so subsequent get-locals/get-callstack/evaluate calls operate on it. Changes debugger selection state, not the debuggee's execution.")]
    public Task<ToolInvocationResult> SelectThread(
        [Description("Thread id as returned by vs_list_threads.")] int threadId,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("select-thread", solution, ("--threadId", threadId.ToString())), null, cancellationToken);

    [McpServerTool(Name = "vs_select_frame", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Selects a stack frame (0-based, innermost-first) within the currently selected thread so subsequent get-locals/evaluate calls operate on it. Selection is only valid until the next continue/step/break. Changes debugger selection state, not the debuggee's execution.")]
    public Task<ToolInvocationResult> SelectFrame(
        [Description("0-based frame index within the selected thread's call stack (frame 0 = innermost/current).")] int index,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("select-frame", solution, ("--index", index.ToString())), null, cancellationToken);

    [McpServerTool(Name = "vs_evaluate", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Evaluates an expression against the currently selected stack frame using the SAFE, non-mutating read path only (Debugger.GetExpression) — never executes statements, property setters, or method calls with side effects. For side-effecting evaluation, use vs_execute_statement instead (requires separate approval).")]
    public Task<ToolInvocationResult> Evaluate(
        [Description("Expression text to evaluate (read-only).")] string expression,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("evaluate", solution, ("--expression", expression)), null, cancellationToken);

    [McpServerTool(Name = "vs_execute_statement", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("UNSAFE: evaluates an expression against the currently selected stack frame via Debugger.ExecuteStatement (--allowSideEffects true), which CAN run property getters, method calls, and assignment statements against the live debuggee. Only use this when side effects are explicitly intended — prefer vs_evaluate for read-only inspection.")]
    public Task<ToolInvocationResult> ExecuteStatement(
        [Description("Expression/statement text to execute. May have side effects on the live debuggee.")] string expression,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("evaluate", solution, ("--expression", expression), ("--allowSideEffects", "true")), null, cancellationToken);
}

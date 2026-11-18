using System.ComponentModel;
using System.Text.Json;
using AgentDebugToolkit.Debugger.Contracts;
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
                ArgumentFormatting.AppendOption(args, flag, value);
            }
        }
        if (solution is not null)
        {
            ArgumentFormatting.AppendOption(args, "--solution", solution);
        }
        return args;
    }

    private Task<ToolInvocationResult> RunAsync(List<string> args, int? timeoutMs, CancellationToken ct, int? instancePid = null)
    {
        if (instancePid is <= 0) return Task.FromResult(Invalid("instancePid must be a positive Visual Studio process ID."));
        if (instancePid is not null) ArgumentFormatting.AppendOption(args, "--instancePid", instancePid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return RunInternalAsync(args, timeoutMs, ct);
    }

    private static ToolInvocationResult Invalid(string message) => new(1,
        JsonSerializer.SerializeToElement(new { success = false, error = "invalid-argument", message }), null, null, null);

    [McpServerTool(Name = "vs_help", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Returns static JSON debugger help, canonical verbs, aliases, options and wait limits. Does not connect to Visual Studio.")]
    public Task<ToolInvocationResult> Help([Description("Optional canonical verb or alias.")] string? verb = null, CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("help", null, ("--verb", verb)), null, cancellationToken);

    [McpServerTool(Name = "vs_list_instances", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Lists ROT-visible Visual Studio instances with instancePid, moniker, observed solution path/mode and per-field errors. Discovery is read-only and does not activate or change debugger state. Busy/unregistered instances can be absent; results are not an atomic process inventory.")]
    public Task<ToolInvocationResult> ListInstances(CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("list-instances", null), null, cancellationToken);

    private async Task<ToolInvocationResult> RunInternalAsync(List<string> args, int? timeoutMs, CancellationToken ct)
    {
        var result = await _cli.RunAsync(Exe, args, timeoutMs, ct);
        return CliRunner.ToInvocationResult(result);
    }

    [McpServerTool(Name = "vs_debugger_status", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Reports the Visual Studio debugger's current mode (design/run/break) and, if stopped at a breakpoint, the last-hit file/line. Read-only.")]
    public Task<ToolInvocationResult> DebuggerStatus(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Opt-in factual instance/thread/frame/exception-local observations. Break cause remains unknown; observations are not atomic.")] bool? context = null,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("debugger-status", solution, ("--context", context?.ToString().ToLowerInvariant())), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_get_callstack", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns the current thread's call stack (innermost frame first). Requires the debugger to be in break mode. Read-only.")]
    public Task<ToolInvocationResult> GetCallStack(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("get-callstack", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_get_locals", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns local variables for the current (innermost) stack frame, or the frame selected via vs_select_frame. Requires break mode. Read-only.")]
    public Task<ToolInvocationResult> GetLocals(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("get-locals", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_get_exception_info", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Reads the exposed $exception local in the selected frame. Requires break mode. Absence does not establish why the debugger broke. Read-only; does not enable expression side effects.")]
    public Task<ToolInvocationResult> GetExceptionInfo(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("get-exception-info", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_continue", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Resumes execution of the active debugging session (Debugger.Go). Requires an active session; does not start a new one. Changes debuggee state.")]
    public Task<ToolInvocationResult> Continue(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("continue", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_step_over", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Steps over one line (Debugger.StepOver). Requires break mode. Changes debuggee state.")]
    public Task<ToolInvocationResult> StepOver(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("step-over", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_step_into", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Steps into one line (Debugger.StepInto). Requires break mode. Changes debuggee state.")]
    public Task<ToolInvocationResult> StepInto(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("step-into", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_step_out", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Steps out of the current function (Debugger.StepOut). Requires break mode. Changes debuggee state.")]
    public Task<ToolInvocationResult> StepOut(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("step-out", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_start_debugging", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Starts a new debugging session (F5 / Debug.Start) for the IDE's configured startup project. Only valid from design mode. Risk: launches a new process.")]
    public Task<ToolInvocationResult> StartDebugging(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("start-debugging", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_stop_debugging", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Stops the active debugging session (Debugger.Stop), terminating the debuggee. Risk: this ends the debuggee process, unlike vs_detach.")]
    public Task<ToolInvocationResult> StopDebugging(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("stop-debugging", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_set_breakpoint", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Adds a breakpoint at the given file/line. Changes debugger state.")]
    public Task<ToolInvocationResult> SetBreakpoint(
        [Description("Absolute source file path.")] string file,
        [Description("1-based line number.")] int line,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("set-breakpoint", solution, ("--file", file), ("--line", line.ToString())), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_list_breakpoints", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Enumerates all currently set breakpoints in the attached Visual Studio instance. Read-only.")]
    public Task<ToolInvocationResult> ListBreakpoints(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("list-breakpoints", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_remove_breakpoint", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Removes one breakpoint (file+line) or every breakpoint (all=true). Changes debugger state.")]
    public Task<ToolInvocationResult> RemoveBreakpoint(
        [Description("Absolute source file path. Omit when all=true.")] string? file = null,
        [Description("1-based line number. Omit when all=true.")] int? line = null,
        [Description("Remove every breakpoint instead of one specific file/line. Mutually exclusive with file/line.")] bool all = false,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs(
            "remove-breakpoint",
            solution,
            ("--file", file),
            ("--line", line?.ToString()),
            ("--all", all ? "true" : null)), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_wait_for_break", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Observes break mode with exactly one timeoutMs (0..590000 milliseconds) or timeoutSeconds (0..590 seconds). Defaults pollMs to 250. Invalid values fail before launch; no silent clamping. Process timeout is requested wait plus 10000ms (at most 600000ms). A COM call can exceed the poll deadline; the process timeout is the outer bound, not a 170-second CLI ceiling. Does not resume, attach, evaluate or change debugger state.")]
    public Task<ToolInvocationResult> WaitForBreak(
        [Description("Wait budget in milliseconds, 0..590000. Supply exactly one of timeoutMs or timeoutSeconds.")] int? timeoutMs = null,
        [Description("Poll interval in milliseconds (default 250).")] int? pollMs = null,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Alternative wait budget in whole seconds, 0..590; mutually exclusive with timeoutMs.")] int? timeoutSeconds = null,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
    {
        var options = new Dictionary<string, string>();
        if (timeoutMs is not null) options["timeoutMs"] = timeoutMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (timeoutSeconds is not null) options["timeoutSeconds"] = timeoutSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (pollMs is not null) options["pollMs"] = pollMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!DebuggerWaitOptions.TryParse(options, DebuggerWaitOptions.MaxMcpTimeoutMs, out var wait, out var error)) return Task.FromResult(Invalid(error!));
        var args = BuildArgs("wait-for-break", solution, ("--timeoutMs", timeoutMs?.ToString()), ("--timeoutSeconds", timeoutSeconds?.ToString()), ("--pollMs", pollMs?.ToString()));
        return RunAsync(args, wait!.ProcessTimeoutMs, cancellationToken, instancePid);
    }

    [McpServerTool(Name = "vs_attach_process", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Attaches the Visual Studio debugger to an already-running process by PID. Changes debugger state; fails with ambiguous-process/process-not-found rather than guessing if the target is unclear.")]
    public Task<ToolInvocationResult> AttachProcess(
        [Description("PID of the target debuggee process.")] int pid,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("attach-process", solution, ("--pid", pid.ToString())), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_break_all", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Breaks all threads in the target debuggee process (not just the current thread). Changes debugger state.")]
    public Task<ToolInvocationResult> BreakAll(
        [Description("PID of the target debuggee process (must already be under an active debug session).")] int pid,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("break-all", solution, ("--pid", pid.ToString())), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_detach", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Detaches the debugger from the target debuggee process without terminating it (Process.Detach, not Debugger.Stop). The debuggee keeps running afterward.")]
    public Task<ToolInvocationResult> Detach(
        [Description("PID of the target debuggee process (must already be under an active debug session).")] int pid,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("detach", solution, ("--pid", pid.ToString())), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_list_threads", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Enumerates the currently-debugged program's threads (id + name). Requires break mode. Read-only.")]
    public Task<ToolInvocationResult> ListThreads(
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("list-threads", solution), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_select_thread", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Selects a thread (by the id returned from vs_list_threads) so subsequent get-locals/get-callstack/evaluate calls operate on it. Changes debugger selection state, not the debuggee's execution.")]
    public Task<ToolInvocationResult> SelectThread(
        [Description("Thread id as returned by vs_list_threads.")] int threadId,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("select-thread", solution, ("--threadId", threadId.ToString())), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_select_frame", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Selects a stack frame (0-based, innermost-first) within the currently selected thread so subsequent get-locals/evaluate calls operate on it. Selection is only valid until the next continue/step/break. Changes debugger selection state, not the debuggee's execution. Error codes: frame-index-out-of-range (index beyond frame count); frame-not-managed (External Code or native frame); frame-selection-stale (debuggee resumed mid-call — clear any firing breakpoints and retry); frame-selection-failed (e.g. 0x80070490 in native/managed-transition regions — known EnvDTE limitation, do not retry); frame-selection-mismatch (EnvDTE silently bound a different frame than requested; message names both). After any failed select-frame, re-select a known-good frame (usually index 0 if managed) and confirm with vs_get_callstack before trusting vs_get_locals or vs_evaluate.")]
    public Task<ToolInvocationResult> SelectFrame(
        [Description("0-based frame index within the selected thread's call stack (frame 0 = innermost/current).")] int index,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("select-frame", solution, ("--index", index.ToString())), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_evaluate", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Evaluates an expression against the currently selected stack frame using the SAFE, non-mutating read path only (Debugger.GetExpression) — never executes statements, property setters, or method calls with side effects. For side-effecting evaluation, use vs_execute_statement instead (requires separate approval).")]
    public Task<ToolInvocationResult> Evaluate(
        [Description("Expression text to evaluate (read-only).")] string expression,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("evaluate", solution, ("--expression", expression)), null, cancellationToken, instancePid);

    [McpServerTool(Name = "vs_execute_statement", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("UNSAFE: evaluates an expression against the currently selected stack frame via Debugger.ExecuteStatement (--allowSideEffects true), which CAN run property getters, method calls, and assignment statements against the live debuggee. Only use this when side effects are explicitly intended — prefer vs_evaluate for read-only inspection.")]
    public Task<ToolInvocationResult> ExecuteStatement(
        [Description("Expression/statement text to execute. May have side effects on the live debuggee.")] string expression,
        [Description("Optional solution file name (without extension) to disambiguate which running devenv.exe instance to use.")] string? solution = null,
        CancellationToken cancellationToken = default,
        [Description("Exact VS process PID from vs_list_instances, not the debuggee PID. No fallback; solution is an additional constraint.")] int? instancePid = null)
        => RunAsync(BuildArgs("evaluate", solution, ("--expression", expression), ("--allowSideEffects", "true")), null, cancellationToken, instancePid);
}

using AgentDebugToolkit.Debugger.Contracts;

namespace AgentDebugToolkit.Debugger.VisualStudio;

internal sealed record InstanceTarget(int? InstancePid, Func<string?> ReadSolutionName);

internal static class DebuggerDiscovery
{
    internal static readonly string[] Verbs = ["info", "help", "list-instances", "debugger-status", "get-callstack", "get-locals", "get-exception-info",
        "continue", "step-over", "step-into", "step-out", "start-debugging", "stop-debugging", "set-breakpoint", "list-breakpoints",
        "remove-breakpoint", "wait-for-break", "attach-process", "break-all", "detach", "list-threads", "select-thread", "select-frame", "evaluate"];

    internal static string Canonicalize(string verb) => verb == "get-call-stack" ? "get-callstack" : verb;

    internal static bool TryHandleHelp(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;
        string? selected = null;
        if (args[0] == "--help")
        {
            if (args.Length != 1) return Fail("--help does not accept additional arguments.", out exitCode);
        }
        else if (args[0] == "help")
        {
            if (args.Length == 3 && args[1] == "--verb") selected = args[2];
            else if (args.Length == 2 && args[1].StartsWith("--verb=", StringComparison.Ordinal)) selected = args[1][7..];
            else if (args.Length != 1) return Fail("Expected help [--verb V].", out exitCode);
        }
        else if (args.Length == 2 && args[1] == "--help") selected = args[0];
        else return false;
        if (selected is not null && !Verbs.Contains(Canonicalize(selected))) return Fail($"Unknown help verb '{selected}'.", out exitCode);
        var names = selected is null ? Verbs : new[] { Canonicalize(selected) };
        JsonOutput.WriteSuccess(new
        {
            tool = "vs", contractVersion = 1,
            verbs = names.Select(name => new
            {
                name, aliases = name == "get-callstack" ? new[] { "get-call-stack" } : Array.Empty<string>(),
                readOnlyByDefault = name is "info" or "help" or "list-instances" or "debugger-status" or "get-callstack" or "get-locals"
                    or "get-exception-info" or "list-breakpoints" or "wait-for-break" or "list-threads" or "evaluate",
                options = Options(name)
            }),
            wait = new { defaultPollMs = 250, maxMcpTimeoutMs = DebuggerWaitOptions.MaxMcpTimeoutMs,
                processHeadroomMs = DebuggerWaitOptions.ProcessHeadroomMs, legacyCliMaxTimeoutMs = int.MaxValue, maxTimeoutSeconds = 590 },
            hint = "Help is static and never connects to Visual Studio. Capabilities are executable features, not permission to mutate a debuggee."
        });
        return true;
    }

    private static string[] Options(string verb)
    {
        string[] specific = verb switch
        {
            "info" => ["--verifyPackage [true|false]"], "help" => ["--verb V"], "list-instances" => [],
            "debugger-status" => ["--context [true|false]"],
            "wait-for-break" => ["--timeoutMs N | --timeoutSeconds S (exactly one)", "--pollMs N (default 250)"],
            "set-breakpoint" => ["--file PATH", "--line N"], "remove-breakpoint" => ["--file PATH --line N | --all"],
            "attach-process" or "break-all" or "detach" => ["--pid N (debuggee PID, not VS instance PID)"],
            "select-thread" => ["--threadId N"], "select-frame" => ["--index N (zero-based)"],
            "evaluate" => ["--expression TEXT", "--allowSideEffects true (separate explicit permission required)"], _ => []
        };
        return verb is "info" or "help" or "list-instances" ? specific : specific.Concat(new[] { "--solution NAME", "--instancePid N (VS PID)" }).ToArray();
    }

    private static bool Fail(string message, out int exitCode)
    {
        JsonOutput.WriteError("invalid-argument", message);
        exitCode = 1;
        return true;
    }

    internal static string? ValidateTarget(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("solution", out var solution) && string.IsNullOrWhiteSpace(solution)) return "An explicit --solution must not be empty or whitespace.";
        if (options.TryGetValue("instancePid", out var pid) && (!int.TryParse(pid, out var value) || value <= 0)) return "--instancePid must be a positive Visual Studio process ID.";
        return null;
    }

    internal static (int? Index, string? Error) SelectInstance(IReadOnlyList<InstanceTarget> candidates, string? solution, int? instancePid)
    {
        var matches = Enumerable.Range(0, candidates.Count).Where(index => instancePid is null || candidates[index].InstancePid == instancePid).ToList();
        if (solution is not null) matches = matches.Where(index => string.Equals(candidates[index].ReadSolutionName(), solution, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return (null, "devenv-not-found");
        if ((instancePid is not null || solution is not null) && matches.Count > 1) return (null, "ambiguous-instance");
        return (matches[0], null);
    }
}
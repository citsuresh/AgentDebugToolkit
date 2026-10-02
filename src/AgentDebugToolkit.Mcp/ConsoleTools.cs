using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AgentDebugToolkit.Mcp;

/// <summary>
/// One MCP tool per agentdebug-console.exe verb, as documented in docs/CLI_CONTRACT.md's
/// "CLI Contract — AgentDebugToolkit.ConsoleAutomation.Cli" section. Each tool only builds an
/// argument list and relays the CLI's own JSON output — no ConPTY/broker logic lives here.
///
/// Opt-in only: these tools are never registered unless "console" is listed in the server's
/// --tools argument (see Program.cs). This is the highest-risk tool group in the server —
/// console_send_text/console_send_keys can drive an arbitrary interactive process (e.g. a shell)
/// with the current user's privileges, and console_launch is gated by an explicit executable
/// allowlist (see <see cref="ResolveLaunchGate"/>) so it cannot be used to start arbitrary
/// processes, let alone shells, without the operator's prior, explicit configuration.
/// </summary>
[McpServerToolType]
public sealed class ConsoleTools
{
    private const string Exe = "agentdebug-console.exe";
    private readonly CliRunner _cli;

    /// <summary>
    /// Executable file names that must never be launched via console_launch, even if an operator
    /// mistakenly lists one in AGENTDEBUG_CONSOLE_ALLOWED_EXE — these are shells/script hosts
    /// whose whole purpose is executing arbitrary further commands, which would defeat the
    /// allowlist's intent of restricting console_launch to one specific, intended target.
    /// </summary>
    private static readonly HashSet<string> BlockedExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wsl.exe", "bash.exe", "sh.exe",
        "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe",
        "powershell_ise.exe", "conhost.exe", "wt.exe", "windowsterminal.exe",
        "msiexec.exe", "regsvr32.exe", "schtasks.exe",
    };

    public ConsoleTools(CliRunner cli)
    {
        _cli = cli;
    }

    private static List<string> BuildArgs(string verb, params (string Flag, string? Value)[] options)
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
        return args;
    }

    private async Task<ToolInvocationResult> RunAsync(List<string> args, int? timeoutMs, CancellationToken ct)
    {
        var result = await _cli.RunAsync(Exe, args, timeoutMs, ct);
        return CliRunner.ToInvocationResult(result);
    }

    [McpServerTool(Name = "console_launch", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("UNSAFE: starts a new process under a ConPTY terminal session (detached broker), e.g. to drive an interactive console tool. Only launches executables explicitly listed in AGENTDEBUG_CONSOLE_ALLOWED_EXE (full paths, shells always blocked); fails cleanly with console-launch-not-allowed otherwise. Not a shortcut for running arbitrary commands — use only for the intended interactive target.")]
    public async Task<ToolInvocationResult> Launch(
        [Description("Full path to the executable to launch. Must exactly match an entry in AGENTDEBUG_CONSOLE_ALLOWED_EXE after full-path resolution; must not be a shell/script host.")] string exe,
        [Description("Optional single argument string passed through as-is to the allowlisted executable.")] string? args = null,
        [Description("Terminal column count (default 120).")] int? cols = null,
        [Description("Terminal row count (default 30).")] int? rows = null,
        CancellationToken cancellationToken = default)
    {
        var gateError = ValidateLaunchTarget(exe);
        if (gateError is not null)
        {
            return gateError;
        }

        return await RunAsync(
            BuildArgs("launch", ("--exe", exe), ("--args", args), ("--cols", cols?.ToString()), ("--rows", rows?.ToString())),
            null,
            cancellationToken);
    }

    /// <summary>
    /// Validates a console_launch target against AGENTDEBUG_CONSOLE_ALLOWED_EXE before the CLI is
    /// ever invoked. Returns a wrapper-level error result if the target is not allowed, or null if
    /// the launch may proceed. This check happens entirely in-process — an unauthorized launch
    /// never reaches agentdebug-console.exe at all.
    /// </summary>
    private static ToolInvocationResult? ValidateLaunchTarget(string exe)
    {
        var allowListRaw = Environment.GetEnvironmentVariable("AGENTDEBUG_CONSOLE_ALLOWED_EXE");
        if (string.IsNullOrWhiteSpace(allowListRaw))
        {
            return new ToolInvocationResult(
                -1,
                null,
                "console-launch-not-allowed",
                "console_launch is disabled: AGENTDEBUG_CONSOLE_ALLOWED_EXE is not set. Set it to a ';'-separated list of full executable paths to allow specific targets.",
                null);
        }

        if (string.IsNullOrWhiteSpace(exe))
        {
            return new ToolInvocationResult(-1, null, "console-launch-not-allowed", "exe must be a non-empty full path.", null);
        }

        // Bare names/relative paths are rejected outright — only an exact, fully-resolved path
        // match against the allowlist is accepted, so a relative "foo.exe" can't accidentally
        // resolve to a different, unintended binary depending on the server's working directory.
        string fullExePath;
        try
        {
            fullExePath = Path.GetFullPath(exe);
        }
        catch (Exception ex)
        {
            return new ToolInvocationResult(-1, null, "console-launch-not-allowed", $"exe is not a valid path: {ex.Message}", null);
        }

        if (!Path.IsPathFullyQualified(exe))
        {
            return new ToolInvocationResult(
                -1, null, "console-launch-not-allowed",
                $"exe must be a full, fully-qualified path (got '{exe}'). Bare names and relative paths are rejected.", null);
        }

        var exeFileName = Path.GetFileName(fullExePath);
        if (BlockedExeNames.Contains(exeFileName))
        {
            return new ToolInvocationResult(
                -1, null, "console-launch-not-allowed",
                $"'{exeFileName}' is a shell/script host and can never be launched via console_launch, even if listed in AGENTDEBUG_CONSOLE_ALLOWED_EXE.", null);
        }

        var allowList = allowListRaw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                try { return Path.GetFullPath(entry); }
                catch { return null; }
            })
            .Where(entry => entry is not null)
            .ToList();

        var isAllowed = allowList.Any(allowed => string.Equals(allowed, fullExePath, StringComparison.OrdinalIgnoreCase));
        if (!isAllowed)
        {
            return new ToolInvocationResult(
                -1, null, "console-launch-not-allowed",
                $"'{fullExePath}' is not listed in AGENTDEBUG_CONSOLE_ALLOWED_EXE. Add its full path to that ';'-separated environment variable to allow it.", null);
        }

        return null;
    }

    [McpServerTool(Name = "console_read_screen", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns the ConPTY terminal buffer's rows (all, or the trailing N if lines is given) for the current console session. Read-only.")]
    public Task<ToolInvocationResult> ReadScreen(
        [Description("Optional count of trailing rows to return; omit for all buffered rows.")] int? lines = null,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("read-screen", ("--lines", lines?.ToString())), null, cancellationToken);

    [McpServerTool(Name = "console_is_running", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Reports whether the console session's target process is still running, and its exit code if not. Read-only.")]
    public Task<ToolInvocationResult> IsRunning(CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("is-running"), null, cancellationToken);

    [McpServerTool(Name = "console_wait_for_text", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Polls the console terminal buffer until pattern matches (literal, or a .NET regex if regex=true), or timeoutMs elapses. Read-only (observational), but blocks for up to timeoutMs.")]
    public Task<ToolInvocationResult> WaitForText(
        [Description("Literal text or (if regex=true) a .NET regular expression to match against the terminal buffer.")] string pattern,
        [Description("Treat pattern as a .NET regular expression instead of literal text.")] bool? regex = null,
        [Description("Maximum time to wait, in milliseconds (default 5000). Capped at 120000 for this tool.")] int? timeoutMs = null,
        [Description("Poll interval in milliseconds (default 250).")] int? pollMs = null,
        CancellationToken cancellationToken = default)
    {
        const int MaxWaitMs = 120_000;
        var cappedTimeoutMs = timeoutMs is null ? (int?)null : Math.Clamp(timeoutMs.Value, 0, MaxWaitMs);
        var args = BuildArgs(
            "wait-for-text",
            ("--pattern", pattern),
            ("--regex", regex is true ? "true" : null),
            ("--timeoutMs", cappedTimeoutMs?.ToString()),
            ("--pollMs", pollMs?.ToString()));
        return RunAsync(args, (cappedTimeoutMs ?? 5000) + 10_000, cancellationToken);
    }

    [McpServerTool(Name = "console_send_text", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("UNSAFE: sends literal UTF-8 text as keyboard input to the console session's target process. If that target is a shell, this can run ANY command the current user is permitted to run, with no further confirmation. Only use to drive the specific interactive tool launched via console_launch — never as a shortcut around other tools.")]
    public Task<ToolInvocationResult> SendText(
        [Description("Text to send as input to the target process.")] string text,
        CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("send-text", ("--text", text)), null, cancellationToken);

    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENTER", "TAB", "ESC", "UP", "DOWN", "LEFT", "RIGHT", "CTRL+C",
    };

    [McpServerTool(Name = "console_send_keys", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Sends one supported terminal key sequence (ENTER, TAB, ESC, UP, DOWN, LEFT, RIGHT, CTRL+C) to the console session's target process. Destructive: can interrupt or submit input to the target the same as a real keypress would.")]
    public Task<ToolInvocationResult> SendKeys(
        [Description("One of: ENTER, TAB, ESC, UP, DOWN, LEFT, RIGHT, CTRL+C.")] string keys,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keys) || !SupportedKeys.Contains(keys))
        {
            return Task.FromResult(new ToolInvocationResult(
                -1, null, "invalid-argument",
                $"'{keys}' is not a supported key. Supported: {string.Join(", ", SupportedKeys)}.", null));
        }

        // Normalize to the CLI's exact casing (CTRL+C) regardless of caller casing, since the
        // supported-key check above is case-insensitive but the underlying CLI expects this form.
        var normalized = keys.Equals("CTRL+C", StringComparison.OrdinalIgnoreCase) ? "CTRL+C" : keys.ToUpperInvariant();
        return RunAsync(BuildArgs("send-keys", ("--keys", normalized)), null, cancellationToken);
    }

    [McpServerTool(Name = "console_stop", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Stops the console session: terminates the target process if needed, drains final output, and clears the session context. Destructive.")]
    public Task<ToolInvocationResult> Stop(CancellationToken cancellationToken = default)
        => RunAsync(BuildArgs("stop"), null, cancellationToken);
}

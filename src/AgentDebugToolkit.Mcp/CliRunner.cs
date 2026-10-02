using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentDebugToolkit.Mcp;

/// <summary>
/// The plain object every MCP tool method returns to the client, serialized by the MCP SDK's
/// default "other types -> JSON text content" rule. Carries the CLI's own JSON verbatim under
/// <see cref="Result"/> on a successful process run (including the CLI's own error envelopes,
/// which are *not* wrapper errors), or a wrapper-level error describing why the CLI could not
/// be run/parsed at all.
/// </summary>
public sealed class ToolInvocationResult
{
    public ToolInvocationResult(int exitCode, JsonElement? result, string? wrapperErrorCode, string? wrapperErrorMessage, string? rawOutput)
    {
        ExitCode = exitCode;
        Result = result;
        WrapperErrorCode = wrapperErrorCode;
        WrapperErrorMessage = wrapperErrorMessage;
        RawOutput = rawOutput;
    }

    /// <summary>Exit code of the child CLI process.</summary>
    public int ExitCode { get; }

    /// <summary>The CLI's own JSON output, unchanged, when the process ran and produced valid JSON.</summary>
    public JsonElement? Result { get; }

    /// <summary>Set only for a wrapper-level failure (exe not found, timeout, non-JSON output) — never for the CLI's own reported errors.</summary>
    public string? WrapperErrorCode { get; }
    public string? WrapperErrorMessage { get; }

    /// <summary>Raw stdout/stderr captured when a wrapper-level error prevented JSON parsing, for diagnostics.</summary>
    public string? RawOutput { get; }
}

/// <summary>
/// Result of running one of the toolkit's CLI exes. Carries the CLI's own parsed JSON
/// (unchanged) on success, or a structured error describing what went wrong, so a caller
/// can always branch on <see cref="IsError"/> / <see cref="ErrorCode"/> instead of parsing
/// prose or catching exceptions.
/// </summary>
public sealed class CliResult
{
    public required bool IsError { get; init; }
    public required int ExitCode { get; init; }

    /// <summary>The CLI's own parsed JSON output, present when the CLI produced valid JSON (success or failure envelope alike).</summary>
    public JsonElement? Json { get; init; }

    /// <summary>Set when this result represents a wrapper-level failure (non-JSON output, timeout, exe not found) rather than the CLI's own reported error.</summary>
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Raw stdout, included on wrapper-level errors so the caller can see what the CLI actually printed.</summary>
    public string? RawOutput { get; init; }
}

/// <summary>
/// Launches the toolkit's existing agentdebug-vs/agentdebug-ui/agentdebug-console CLIs as
/// child processes and relays their JSON output unchanged. This class contains no debugger,
/// COM, or UIA logic of its own — it is a thin process-launching wrapper only.
/// </summary>
public sealed class CliRunner
{
    private const int MaxTimeoutMs = 10 * 60 * 1000; // 10 minutes — sane cap for long-running verbs like wait-for-break.
    private const int DefaultTimeoutMs = 30_000;

    private readonly ILogger _logger;
    private readonly string _vsBinDirectory;
    private readonly string _uiBinDirectory;
    private readonly string? _consoleBinDirectory;

    public CliRunner(ILogger logger, string vsBinDirectory, string uiBinDirectory, string? consoleBinDirectory = null)
    {
        _logger = logger;
        _vsBinDirectory = vsBinDirectory;
        _uiBinDirectory = uiBinDirectory;
        _consoleBinDirectory = consoleBinDirectory;
    }

    /// <summary>
    /// Resolves the bin directory to search for one of the toolkit's CLI exes. Checks, in
    /// order: the exe-specific override (AGENTDEBUG_VS_BIN / AGENTDEBUG_UI_BIN), then the
    /// shared AGENTDEBUG_TOOLKIT_BIN override, then the folder containing this server's own
    /// build output. Never searches the disk or guesses another path.
    /// </summary>
    public static string ResolveBinDirectory(ILogger logger, string specificEnvVarName)
    {
        var fromSpecific = Environment.GetEnvironmentVariable(specificEnvVarName);
        if (!string.IsNullOrWhiteSpace(fromSpecific))
        {
            logger.LogInformation("Using {EnvVar}={Dir}", specificEnvVarName, fromSpecific);
            return fromSpecific;
        }

        var fromShared = Environment.GetEnvironmentVariable("AGENTDEBUG_TOOLKIT_BIN");
        if (!string.IsNullOrWhiteSpace(fromShared))
        {
            logger.LogInformation("{EnvVar} not set; using AGENTDEBUG_TOOLKIT_BIN={Dir}", specificEnvVarName, fromShared);
            return fromShared;
        }

        var fallback = AppContext.BaseDirectory;
        logger.LogInformation(
            "Neither {EnvVar} nor AGENTDEBUG_TOOLKIT_BIN is set; falling back to server build output directory {Dir}",
            specificEnvVarName, fallback);
        return fallback;
    }

    private string ResolveBinDirectoryForExe(string exeName) => exeName switch
    {
        "agentdebug-vs.exe" => _vsBinDirectory,
        "agentdebug-ui.exe" => _uiBinDirectory,
        "agentdebug-console.exe" => _consoleBinDirectory
            ?? throw new InvalidOperationException("agentdebug-console.exe was requested but no console bin directory was configured."),
        _ => throw new ArgumentOutOfRangeException(nameof(exeName), exeName, "Unknown CLI exe name."),
    };

    private static (string SpecificEnvVarName, string Label) GetEnvVarNames(string exeName) => exeName switch
    {
        "agentdebug-vs.exe" => ("AGENTDEBUG_VS_BIN", "agentdebug-vs.exe"),
        "agentdebug-ui.exe" => ("AGENTDEBUG_UI_BIN", "agentdebug-ui.exe"),
        "agentdebug-console.exe" => ("AGENTDEBUG_CONSOLE_BIN", "agentdebug-console.exe"),
        _ => throw new ArgumentOutOfRangeException(nameof(exeName), exeName, "Unknown CLI exe name."),
    };

    /// <summary>
    /// Builds a human-readable description of every path that was checked for the given exe,
    /// for use in the exe-not-found error message (never guesses beyond these — just reports
    /// them).
    /// </summary>
    private static string BuildCheckedPathsDescription(string exeName)
    {
        var (specificEnvVarName, _) = GetEnvVarNames(exeName);
        var specific = Environment.GetEnvironmentVariable(specificEnvVarName);
        var shared = Environment.GetEnvironmentVariable("AGENTDEBUG_TOOLKIT_BIN");
        var fallback = AppContext.BaseDirectory;

        var checkedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(specific))
        {
            checkedPaths.Add(Path.Combine(specific, exeName) + $" ({specificEnvVarName})");
        }
        if (!string.IsNullOrWhiteSpace(shared))
        {
            checkedPaths.Add(Path.Combine(shared, exeName) + " (AGENTDEBUG_TOOLKIT_BIN)");
        }
        checkedPaths.Add(Path.Combine(fallback, exeName) + " (server build output fallback)");
        return string.Join("; ", checkedPaths);
    }

    /// <summary>
    /// Runs the given CLI exe (e.g. "agentdebug-vs.exe") with the given arguments and returns
    /// its parsed JSON output, or a structured error. Clamps <paramref name="timeoutMs"/> to
    /// a sane maximum and kills the child process if it is exceeded.
    /// </summary>
    public async Task<CliResult> RunAsync(string exeName, IReadOnlyList<string> arguments, int? timeoutMs, CancellationToken cancellationToken)
    {
        var effectiveTimeoutMs = Math.Clamp(timeoutMs ?? DefaultTimeoutMs, 1, MaxTimeoutMs);

        var binDirectory = ResolveBinDirectoryForExe(exeName);
        var exePath = Path.Combine(binDirectory, exeName);
        if (!File.Exists(exePath))
        {
            var (specificEnvVarName, _) = GetEnvVarNames(exeName);
            var checkedPaths = BuildCheckedPathsDescription(exeName);
            var message = $"Could not find '{exeName}'. Checked: {checkedPaths}. " +
                $"Set {specificEnvVarName} (or the shared AGENTDEBUG_TOOLKIT_BIN) to the folder containing the built AgentDebugToolkit CLI exes.";
            _logger.LogError("{Message}", message);
            return new CliResult
            {
                IsError = true,
                ExitCode = -1,
                ErrorCode = "exe-not-found",
                ErrorMessage = message,
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Known toolkit gotcha: a shell/process launched from within Visual Studio can inherit
        // a DOTNET_ROOT(_X64) override pinned to VS's own bundled runtime, which hides the
        // machine-wide runtime the CLIs target and makes them fail to launch.
        startInfo.Environment.Remove("DOTNET_ROOT");
        startInfo.Environment.Remove("DOTNET_ROOT_X64");
        startInfo.Environment.Remove("DOTNET_ROOT_X86");

        _logger.LogInformation("Running {Exe} {Args} (timeoutMs={Timeout})", exeName, string.Join(' ', arguments), effectiveTimeoutMs);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            var message = $"Failed to start '{exePath}': {ex.Message}";
            _logger.LogError(ex, "{Message}", message);
            return new CliResult { IsError = true, ExitCode = -1, ErrorCode = "process-start-failed", ErrorMessage = message };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(effectiveTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            var timedOutByCaller = cancellationToken.IsCancellationRequested;
            TryKill(process);

            var message = timedOutByCaller
                ? $"'{exeName}' was cancelled by the caller after it did not complete."
                : $"'{exeName}' did not complete within {effectiveTimeoutMs}ms and was terminated.";
            _logger.LogWarning("{Message}", message);
            return new CliResult
            {
                IsError = true,
                ExitCode = -1,
                ErrorCode = "timeout",
                ErrorMessage = message,
                RawOutput = stdout.ToString(),
            };
        }

        var rawOutput = stdout.ToString().Trim();
        var rawError = stderr.ToString().Trim();
        if (rawError.Length > 0)
        {
            _logger.LogWarning("{Exe} stderr: {Stderr}", exeName, rawError);
        }

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            var message = $"'{exeName}' exited with code {process.ExitCode} and produced no stdout output.";
            return new CliResult
            {
                IsError = true,
                ExitCode = process.ExitCode,
                ErrorCode = "no-output",
                ErrorMessage = message,
                RawOutput = rawError,
            };
        }

        JsonElement parsed;
        try
        {
            using var doc = JsonDocument.Parse(rawOutput);
            parsed = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            var message = $"'{exeName}' exited with code {process.ExitCode} and did not print valid JSON: {ex.Message}";
            _logger.LogError(ex, "{Message}", message);
            return new CliResult
            {
                IsError = true,
                ExitCode = process.ExitCode,
                ErrorCode = "non-json-output",
                ErrorMessage = message,
                RawOutput = rawOutput,
            };
        }

        // The CLI's own JSON (success or its own error envelope) is passed through unchanged —
        // it is not a wrapper-level error even if the CLI reported "success": false.
        return new CliResult
        {
            IsError = false,
            ExitCode = process.ExitCode,
            Json = parsed,
        };
    }

    /// <summary>
    /// Converts a <see cref="CliResult"/> into the plain object every MCP tool method returns.
    /// On success this embeds the CLI's own JSON unchanged under "result" (including the CLI's
    /// own "success": false error envelopes, e.g. com-busy-retry-exhausted/frame-selection-stale/
    /// process-not-found) alongside the process exit code. On a wrapper-level failure (non-JSON
    /// output, timeout, missing exe) it instead reports a structured wrapper error.
    /// </summary>
    public static ToolInvocationResult ToInvocationResult(CliResult cliResult) => cliResult.IsError
        ? new ToolInvocationResult(cliResult.ExitCode, null, cliResult.ErrorCode, cliResult.ErrorMessage, cliResult.RawOutput)
        : new ToolInvocationResult(cliResult.ExitCode, cliResult.Json, null, null, null);

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill timed-out child process.");
        }
    }
}

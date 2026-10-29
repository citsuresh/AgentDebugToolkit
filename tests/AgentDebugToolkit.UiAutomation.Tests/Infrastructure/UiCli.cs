using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AgentDebugToolkit.UiAutomation.Tests.Infrastructure;

internal sealed record CliResult(int ExitCode, JsonElement Json, string RawOutput)
{
    public bool Success => Json.GetProperty("success").GetBoolean();

    public string? ErrorCode => Json.TryGetProperty("error", out var error) ? error.GetString() : null;
}

/// <summary>Runs the built agentdebug-ui executable and parses its JSON envelope.</summary>
internal static class UiCli
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    public static CliResult Run(params string[] args)
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "agentdebug-ui.exe");
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("agentdebug-ui.exe was not copied to the test output.", exePath);
        }

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("agentdebug-ui.exe did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"agentdebug-ui.exe '{args.FirstOrDefault()}' did not exit within {ProcessTimeout.TotalSeconds}s.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        var jsonLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'))
            ?? throw new InvalidOperationException($"No JSON output. stdout: {stdout} stderr: {stderr}");
        using var document = JsonDocument.Parse(jsonLine);
        return new CliResult(process.ExitCode, document.RootElement.Clone(), stdout);
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentDebugToolkit.ConsoleAutomation.Cli;

internal static class ConsoleVerbs
{
    private const int DefaultColumns = 120;
    private const int DefaultRows = 30;
    private const int DefaultPollMs = 250;
    private const string LaunchMutexName = @"Local\AgentDebugToolkit.ConsoleAutomation.Launch";

    public static int Launch(IReadOnlyDictionary<string, string> options)
    {
        using var launchMutex = new Mutex(false, LaunchMutexName);
        var lockAcquired = false;
        try
        {
            try
            {
                lockAcquired = launchMutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                lockAcquired = true;
            }

            if (!lockAcquired)
            {
                JsonOutput.WriteError("launch-in-progress", "Another console launch is in progress.");
                return 1;
            }

            return LaunchCore(options);
        }
        finally
        {
            if (lockAcquired)
            {
                launchMutex.ReleaseMutex();
            }
        }
    }

    private static int LaunchCore(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("exe", out var executable) || string.IsNullOrWhiteSpace(executable))
        {
            JsonOutput.WriteError("invalid-argument", "--exe is required.");
            return 1;
        }

        if (!File.Exists(executable))
        {
            JsonOutput.WriteError("invalid-argument", $"Target executable was not found: '{executable}'.");
            return 1;
        }

        if (!CommandLine.TryGetPositiveInteger(options, "cols", DefaultColumns, out var columns)
            || !CommandLine.TryGetPositiveInteger(options, "rows", DefaultRows, out var rows))
        {
            JsonOutput.WriteError("invalid-argument", "--cols and --rows must be positive integers.");
            return 1;
        }

        var existing = ConsoleSessionContext.Load();
        if (existing is not null && IsMatchingBroker(existing.BrokerPid, existing.BrokerStartedAtUtc))
        {
            JsonOutput.WriteError("already-running", "A console broker session is already active.");
            return 1;
        }

        if (existing is not null)
        {
            ConsoleSessionContext.ClearIfCurrent(existing);
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var pipeName = $"agentdebugtoolkit-console-{sessionId}";
        Process? broker = null;
        var sessionSaved = false;
        try
        {
            broker = StartBroker(sessionId, executable, options.GetValueOrDefault("args", ""), columns, rows);
            using var response = BrokerClient.SendRequest(pipeName, new { command = "get-session-info" });
            if (!response.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean()
                || !response.RootElement.TryGetProperty("pid", out var pid))
            {
                JsonOutput.WriteError("broker-unreachable", "The started broker did not return target process information.");
                return 1;
            }

            try
            {
                ConsoleSessionContext.Save(new ConsoleSessionContext.Data(
                    sessionId,
                    broker.Id,
                    broker.StartTime.ToUniversalTime(),
                    pipeName,
                    pid.GetInt32()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                JsonOutput.WriteError("session-write-failed", $"Could not persist the console session: {ex.Message}");
                return 1;
            }

            sessionSaved = true;
            JsonOutput.WriteSuccess(new { sessionId, pid = pid.GetInt32() });
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            JsonOutput.WriteError("broker-unreachable", $"Could not connect to the started console broker: {ex.Message}");
            return 1;
        }
        finally
        {
            if (!sessionSaved && broker is not null)
            {
                StopUnpersistedBroker(broker);
            }

            broker?.Dispose();
        }
    }

    private static Process StartBroker(string sessionId, string executable, string arguments, int columns, int rows)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the current executable path.");
        var startInfo = new ProcessStartInfo
        {
            FileName = currentExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (Path.GetFileNameWithoutExtension(currentExecutable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        startInfo.ArgumentList.Add("--broker");
        startInfo.ArgumentList.Add("--sessionId");
        startInfo.ArgumentList.Add(sessionId);
        startInfo.ArgumentList.Add("--exe");
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add("--args");
        startInfo.ArgumentList.Add(arguments);
        startInfo.ArgumentList.Add("--cols");
        startInfo.ArgumentList.Add(columns.ToString());
        startInfo.ArgumentList.Add("--rows");
        startInfo.ArgumentList.Add(rows.ToString());

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the console broker process.");
    }

    private static bool IsMatchingBroker(int pid, DateTime startedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() == startedAtUtc;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void StopUnpersistedBroker(Process broker)
    {
        try
        {
            if (!broker.HasExited)
            {
                broker.Kill(entireProcessTree: true);
                broker.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The broker exited between checking its state and terminating it.
        }
    }

    public static int ReadScreen(IReadOnlyDictionary<string, string> options)
    {
        int? lines = null;
        if (options.TryGetValue("lines", out var requestedLines)
            && (!int.TryParse(requestedLines, out var parsedLines) || parsedLines <= 0))
        {
            JsonOutput.WriteError("invalid-argument", "--lines must be a positive integer.");
            return 1;
        }
        else if (options.ContainsKey("lines"))
        {
            lines = int.Parse(options["lines"]);
        }

        var session = RequireSession();
        if (session is null) return 1;
        using var response = SendRequest(session, new { command = "get-screen-snapshot", lines });
        if (response is null) return 1;

        JsonOutput.WriteSuccess(new { lines = response.RootElement.GetProperty("lines").Clone() });
        return 0;
    }

    public static int SendText(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("text", out var text))
        {
            JsonOutput.WriteError("invalid-argument", "--text is required.");
            return 1;
        }

        return SendTextCore(text);
    }

    public static int SendKeys(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("keys", out var keys) || !TryMapKey(keys, out var text))
        {
            JsonOutput.WriteError(
                "invalid-argument",
                "--keys must be one of ENTER, TAB, ESC, UP, DOWN, LEFT, RIGHT, or CTRL+C.");
            return 1;
        }

        return SendTextCore(text);
    }

    public static int WaitForText(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("pattern", out var pattern) || string.IsNullOrEmpty(pattern))
        {
            JsonOutput.WriteError("invalid-argument", "--pattern is required.");
            return 1;
        }

        if (!CommandLine.TryGetPositiveInteger(options, "timeoutMs", 5000, out var timeoutMs)
            || !CommandLine.TryGetPositiveInteger(options, "pollMs", DefaultPollMs, out var pollMs))
        {
            JsonOutput.WriteError("invalid-argument", "--timeoutMs and --pollMs must be positive integers.");
            return 1;
        }

        var useRegex = options.ContainsKey("regex");
        if (useRegex)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(1));
            }
            catch (ArgumentException ex)
            {
                JsonOutput.WriteError("invalid-argument", $"Invalid --pattern regex: {ex.Message}");
                return 1;
            }
        }

        var session = RequireSession();
        if (session is null) return 1;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            var remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                break;
            }

            JsonDocument response;
            try
            {
                using var requestTimeout = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(remainingMs));
                response = BrokerClient.SendRequestAsync(
                    session.PipeName,
                    new { command = "get-screen-snapshot" },
                    requestTimeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                ConsoleSessionContext.ClearIfCurrent(session);
                JsonOutput.WriteError("broker-unreachable", $"Could not reach the active console broker: {ex.Message}");
                return 1;
            }

            using (response)
            {
            var screen = string.Join(Environment.NewLine,
                response.RootElement.GetProperty("lines").EnumerateArray().Select(line => line.GetString()));
            remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                break;
            }

            bool matched;
            try
            {
                matched = useRegex
                    ? Regex.IsMatch(
                        screen,
                        pattern,
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(remainingMs))
                    : screen.Contains(pattern, StringComparison.Ordinal);
            }
            catch (RegexMatchTimeoutException)
            {
                JsonOutput.WriteError("timeout", $"Text pattern was not found within {timeoutMs}ms.");
                return 1;
            }

            if (matched && stopwatch.ElapsedMilliseconds <= timeoutMs)
            {
                JsonOutput.WriteSuccess(new { matched = true, elapsedMs = stopwatch.ElapsedMilliseconds });
                return 0;
            }
            }

            remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                break;
            }

            Thread.Sleep((int)Math.Min(pollMs, remainingMs));
        } while (true);

        JsonOutput.WriteError("timeout", $"Text pattern was not found within {timeoutMs}ms.");
        return 1;
    }

    public static int IsRunning(IReadOnlyDictionary<string, string> options)
    {
        var session = RequireSession();
        if (session is null) return 1;
        using var response = SendRequest(session, new { command = "is-running" });
        if (response is null) return 1;

        JsonOutput.WriteSuccess(new
        {
            running = response.RootElement.GetProperty("running").GetBoolean(),
            exitCode = response.RootElement.GetProperty("exitCode").Clone()
        });
        return 0;
    }

    public static int Stop(IReadOnlyDictionary<string, string> options)
    {
        var session = RequireSession();
        if (session is null) return 1;
        using var response = SendRequest(session, new { command = "shutdown" });
        if (response is null) return 1;

        ConsoleSessionContext.ClearIfCurrent(session);
        JsonOutput.WriteSuccess(new { stopped = true });
        return 0;
    }

    private static int SendTextCore(string text)
    {
        var session = RequireSession();
        if (session is null) return 1;
        using var response = SendRequest(session, new { command = "send-text", text });
        if (response is null) return 1;
        if (!response.RootElement.GetProperty("success").GetBoolean())
        {
            JsonOutput.WriteError(
                response.RootElement.GetProperty("error").GetString()!,
                response.RootElement.GetProperty("message").GetString()!);
            return 1;
        }

        JsonOutput.WriteSuccess(new { sent = true });
        return 0;
    }

    private static ConsoleSessionContext.Data? RequireSession()
    {
        var session = ConsoleSessionContext.Load();
        if (session is null)
        {
            JsonOutput.WriteError("session-not-found", "No active console session was found.");
            return null;
        }

        if (!IsMatchingBroker(session.BrokerPid, session.BrokerStartedAtUtc))
        {
            ConsoleSessionContext.ClearIfCurrent(session);
            JsonOutput.WriteError("session-not-found", "The persisted console broker is no longer running.");
            return null;
        }

        return session;
    }

    private static JsonDocument? SendRequest(ConsoleSessionContext.Data session, object request)
    {
        try
        {
            return BrokerClient.SendRequest(session.PipeName, request);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            ConsoleSessionContext.ClearIfCurrent(session);
            JsonOutput.WriteError("broker-unreachable", $"Could not reach the active console broker: {ex.Message}");
            return null;
        }
    }

    private static bool TryMapKey(string key, out string text)
    {
        text = key.ToUpperInvariant() switch
        {
            "ENTER" => "\r",
            "TAB" => "\t",
            "ESC" => "\x1b",
            "UP" => "\x1b[A",
            "DOWN" => "\x1b[B",
            "LEFT" => "\x1b[D",
            "RIGHT" => "\x1b[C",
            "CTRL+C" => "\x03",
            _ => string.Empty
        };
        return text.Length > 0;
    }
}

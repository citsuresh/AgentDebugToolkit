namespace AgentDebugToolkit.Debugger.Contracts;

internal sealed record DebuggerWaitOptions(int TimeoutMs, int PollMs)
{
    internal const int MaxMcpTimeoutMs = 590000;
    internal const int ProcessHeadroomMs = 10000;
    internal int ProcessTimeoutMs => checked(TimeoutMs + ProcessHeadroomMs);

    internal static bool TryParse(IReadOnlyDictionary<string, string> options, int maxTimeoutMs,
        out DebuggerWaitOptions? result, out string? error)
    {
        result = null;
        error = null;
        var milliseconds = options.TryGetValue("timeoutMs", out var ms);
        var seconds = options.TryGetValue("timeoutSeconds", out var sec);
        if (milliseconds == seconds)
        {
            error = "Supply exactly one of --timeoutMs (milliseconds) or --timeoutSeconds (seconds).";
            return false;
        }
        if (!int.TryParse(milliseconds ? ms : sec, out var value) || value < 0
            || (seconds && value > MaxMcpTimeoutMs / 1000) || (milliseconds && value > maxTimeoutMs))
        {
            error = seconds ? "--timeoutSeconds must be an integer from 0 to 590 seconds."
                : $"--timeoutMs must be an integer from 0 to {maxTimeoutMs} milliseconds.";
            return false;
        }
        var poll = 250;
        if (options.TryGetValue("pollMs", out var text) && (!int.TryParse(text, out poll) || poll <= 0))
        {
            error = "--pollMs must be a positive integer in milliseconds.";
            return false;
        }
        result = new(seconds ? checked(value * 1000) : value, poll);
        return true;
    }

    internal static bool Poll(Func<bool> isBreak, Func<long> elapsedMs, Action<int> sleep, DebuggerWaitOptions options)
    {
        while (true)
        {
            if (isBreak()) return true;
            var elapsed = elapsedMs();
            if (elapsed >= options.TimeoutMs) return false;
            sleep((int)Math.Min(options.PollMs, options.TimeoutMs - elapsed));
        }
    }
}
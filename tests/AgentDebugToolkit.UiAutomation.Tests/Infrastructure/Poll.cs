using System.Diagnostics;

namespace AgentDebugToolkit.UiAutomation.Tests.Infrastructure;

internal static class Poll
{
    /// <summary>Re-evaluates <paramref name="condition"/> until it holds or the timeout elapses.</summary>
    public static bool Until(Func<bool> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var pollInterval = interval ?? TimeSpan.FromMilliseconds(50);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (clock.Elapsed >= timeout)
            {
                return false;
            }

            Thread.Sleep(pollInterval);
        }
    }
}

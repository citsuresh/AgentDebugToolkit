using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ScrollPositionPollingTests
{
    [Fact]
    public void PollForScrollPosition_CapsFinalSleepAndDoesNotReadAfterDeadline()
    {
        long elapsedMilliseconds = 487;
        var readCount = 0;
        var sleepDurations = new List<int>();

        var position = Verbs.PollForScrollPosition(
            () =>
            {
                readCount++;
                return 25;
            },
            requestedPercent: 100,
            () => elapsedMilliseconds,
            milliseconds =>
            {
                sleepDurations.Add(milliseconds);
                elapsedMilliseconds += milliseconds;
            });

        Assert.Equal(25, position);
        Assert.Equal(1, readCount);
        Assert.Equal(new[] { 13 }, sleepDurations);
        Assert.Equal(500, elapsedMilliseconds);
    }
}

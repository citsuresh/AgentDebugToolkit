using System.Windows;
using System.Windows.Controls;
using AgentDebugToolkit.UiAutomation.Cli;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

/// <summary>
/// The mouse-wheel <c>scroll</c> verb. Argument and window checks are deterministic; the tests
/// that really send wheel input carry the InteractiveDesktop trait because they move the real
/// cursor and need a foreground switch.
/// </summary>
public sealed class ScrollWheelTests
{
    [Fact]
    public void Scroll_RequiresHwnd()
    {
        var result = UiCli.Run("scroll", "--direction", "down");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--hwnd", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("sideways")]
    public void Scroll_RequiresValidDirection(string direction)
    {
        var args = direction.Length == 0
            ? new[] { "scroll", "--hwnd", "0x1" }
            : new[] { "scroll", "--hwnd", "0x1", "--direction", direction };

        var result = UiCli.Run(args);

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--direction", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("-3")]
    [InlineData("many")]
    public void Scroll_RejectsOutOfRangeClicks(string clicks)
    {
        var result = UiCli.Run("scroll", "--hwnd", "0x1", "--direction", "down", $"--clicks={clicks}");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--clicks", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("--x", "10")]
    [InlineData("--y", "10")]
    public void Scroll_RequiresBothCoordinates(string flag, string value)
    {
        var result = UiCli.Run("scroll", "--hwnd", "0x1", "--direction", "down", flag, value);

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--x and --y", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("--allowOutsideWindow")]
    [InlineData("--allowOutsideWindow=true")]
    [InlineData("--allowOutsideWindow=false")]
    public void Scroll_AllowOutsideWindowRequiresCoordinates(string option)
    {
        var result = UiCli.Run("scroll", "--hwnd", "0x1", "--direction", "down", option);

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("only valid with --x/--y", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void Scroll_RejectsNonBooleanAllowOutsideWindow()
    {
        var result = UiCli.Run("scroll", "--hwnd", "0x1", "--direction", "down", "--x", "1", "--y", "1",
            "--allowOutsideWindow=maybe");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("true or false", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void Scroll_RejectsInvalidHwnd()
    {
        var result = UiCli.Run("scroll", "--hwnd", "not-a-window", "--direction", "down");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("Invalid hwnd", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void Scroll_ClosedWindowReturnsWindowGone()
    {
        var window = FixtureWindow.Create(() => new ScrollViewer());
        var staleHwnd = window.HwndArgument;
        window.Dispose();

        var result = UiCli.Run("scroll", "--hwnd", staleHwnd, "--direction", "down");

        Assert.Equal("window-gone", result.ErrorCode);
    }

    [Fact]
    public void Scroll_RejectsPointOutsideWindowBeforeAnyInput()
    {
        ScrollViewer viewer = null!;
        using var window = FixtureWindow.Create(() => viewer = TallViewer());
        var foregroundBefore = NativeMethods.GetForegroundWindow();

        var result = UiCli.Run("scroll", "--hwnd", window.HwndArgument, "--direction", "down",
            "--x", "-11", "--y", "-11");

        // The point is rejected before any activation: the fixture was not brought to the front.
        Assert.Equal(foregroundBefore, NativeMethods.GetForegroundWindow());
        Assert.NotEqual(window.Hwnd, NativeMethods.GetForegroundWindow());

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("outside the target window rectangle", result.Json.GetProperty("message").GetString());
        Assert.Equal(0d, window.Invoke(() => viewer.VerticalOffset));
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void Scroll_DownThenUpMovesTheViewerAtTheWindowCenter()
    {
        ScrollViewer viewer = null!;
        using var window = FixtureWindow.Create(() => viewer = TallViewer());
        Assert.Equal(0d, window.Invoke(() => viewer.VerticalOffset));

        var down = UiCli.Run("scroll", "--hwnd", window.HwndArgument, "--direction", "down", "--clicks", "3");

        Assert.True(down.Success, down.RawOutput);
        Assert.Equal("synthetic-wheel", down.Json.GetProperty("method").GetString());
        Assert.Equal(3, down.Json.GetProperty("clicks").GetInt32());
        Assert.True(Poll.Until(() => window.Invoke(() => viewer.VerticalOffset) > 0, TimeSpan.FromSeconds(5)),
            "The viewer did not scroll down.");
        var offset = window.Invoke(() => viewer.VerticalOffset);

        var up = UiCli.Run("scroll", "--hwnd", window.HwndArgument, "--direction", "up", "--clicks", "3");

        Assert.True(up.Success, up.RawOutput);
        Assert.True(Poll.Until(() => window.Invoke(() => viewer.VerticalOffset) < offset, TimeSpan.FromSeconds(5)),
            "The viewer did not scroll back up.");
    }

    [Theory]
    [InlineData("right", 1)]
    [InlineData("left", -1)]
    [Trait("Category", "InteractiveDesktop")]
    public void Scroll_HorizontalDirectionsDeliverHorizontalWheelMessages(string direction, int sign)
    {
        // WPF's ScrollViewer ignores WM_MOUSEHWHEEL, so observe the message itself: the verb's
        // job is to deliver horizontal wheel notches with the right sign.
        const int WM_MOUSEHWHEEL = 0x020E;
        var deltas = new List<int>();
        using var window = FixtureWindow.Create(WideViewer);
        window.Invoke(() =>
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(window.Hwnd)!;
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_MOUSEHWHEEL)
                {
                    lock (deltas)
                    {
                        deltas.Add(unchecked((short)((long)wParam >> 16)));
                    }
                }

                return IntPtr.Zero;
            });
        });

        var result = UiCli.Run("scroll", "--hwnd", window.HwndArgument, "--direction", direction, "--clicks", "2");

        Assert.True(result.Success, result.RawOutput);
        Assert.True(Poll.Until(() => { lock (deltas) { return deltas.Count >= 2; } }, TimeSpan.FromSeconds(5)),
            "The window did not receive two horizontal wheel messages.");
        Thread.Sleep(400); // let any extra notch arrive, so the count below is exact
        lock (deltas)
        {
            Assert.Equal(2, deltas.Count);
            Assert.All(deltas, delta => Assert.Equal(sign * 120, delta));
        }
    }

    private static ScrollViewer TallViewer() => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new Border { Height = 5000, Width = 300, Background = System.Windows.Media.Brushes.LightGray },
    };

    private static ScrollViewer WideViewer() => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new Border { Width = 5000, Height = 200, Background = System.Windows.Media.Brushes.LightGray },
    };
}

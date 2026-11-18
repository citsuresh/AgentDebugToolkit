using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AgentDebugToolkit.UiAutomation.Cli;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class PointerCoordinateTargetTests
{
    [Theory]
    [InlineData("click")]
    [InlineData("right-click")]
    [InlineData("double-click")]
    public void PointerVerbs_RequireExactlyOneTargetForm(string verb)
    {
        var result = UiCli.Run(verb, "--hwnd", "0x1");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("Exactly one", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right-click")]
    [InlineData("double-click")]
    public void PointerVerbs_RejectPartialCoordinates(string verb)
    {
        var result = UiCli.Run(verb, "--hwnd", "0x1", "--x", "10");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--x and --y", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right-click")]
    [InlineData("double-click")]
    public void PointerVerbs_RejectSelectorAndCoordinateTargetsTogether(string verb)
    {
        var result = UiCli.Run(verb, "--hwnd", "0x1", "--strategy", "Name", "--value", "Target",
            "--x", "10", "--y", "20");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("Exactly one", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right-click")]
    [InlineData("double-click")]
    public void PointerVerbs_AcceptCoordinatesWithoutSelector(string verb)
    {
        var result = UiCli.Run(verb, "--hwnd", "not-a-window", "--x", "-10", "--y", "20");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("Invalid hwnd", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right-click")]
    [InlineData("double-click")]
    [Trait("Category", "InteractiveDesktop")]
    public void PointerVerbs_RejectNegativePointOutsideWindowBeforeInput(string verb)
    {
        Button button = null!;
        var mouseDownCount = 0;
        var activationCount = 0;
        var stateChangeCount = 0;
        using var window = FixtureWindow.Create(() =>
        {
            button = new Button { Content = "Target" };
            button.PreviewMouseDown += (_, _) => mouseDownCount++;
            return button;
        });
        window.Invoke(() =>
        {
            var target = Window.GetWindow(button);
            Assert.False(target.IsActive);
            Assert.Equal(WindowState.Normal, target.WindowState);
            target.Activated += (_, _) => activationCount++;
            target.StateChanged += (_, _) => stateChangeCount++;
        });
        Assert.False(NativeMethods.IsIconic(window.Hwnd));
        Assert.True(NativeMethods.GetWindowRect(window.Hwnd, out var bounds));
        Assert.True(-11 < bounds.Left || -11 < bounds.Top);

        var result = UiCli.Run(verb, "--hwnd", window.HwndArgument, "--x", "-11", "--y", "-11");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("outside the target window rectangle", result.Json.GetProperty("message").GetString());
        Assert.Equal(0, window.Invoke(() => mouseDownCount));
        Assert.Equal(0, window.Invoke(() => activationCount));
        Assert.Equal(0, window.Invoke(() => stateChangeCount));
        Assert.Equal(WindowState.Normal, window.Invoke(() => Window.GetWindow(button).WindowState));
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right-click")]
    [InlineData("double-click")]
    [Trait("Category", "InteractiveDesktop")]
    public void PointerVerbs_RestoreMinimizedWindowBeforeRejectingOutsidePoint(string verb)
    {
        Button button = null!;
        var mouseDownCount = 0;
        var restoreCount = 0;
        using var window = FixtureWindow.Create(() =>
        {
            button = new Button { Content = "Target" };
            button.PreviewMouseDown += (_, _) => mouseDownCount++;
            return button;
        });
        window.Invoke(() =>
        {
            var target = Window.GetWindow(button);
            target.WindowState = WindowState.Minimized;
            target.StateChanged += (_, _) =>
            {
                if (target.WindowState == WindowState.Normal)
                {
                    restoreCount++;
                }
            };
        });
        Assert.True(NativeMethods.IsIconic(window.Hwnd));

        var result = UiCli.Run(verb, "--hwnd", window.HwndArgument, "--x", "-11", "--y", "-11");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("outside the target window rectangle", result.Json.GetProperty("message").GetString());
        Assert.Equal(1, window.Invoke(() => restoreCount));
        Assert.Equal(WindowState.Normal, window.Invoke(() => Window.GetWindow(button).WindowState));
        Assert.False(NativeMethods.IsIconic(window.Hwnd));
        Assert.Equal(0, window.Invoke(() => mouseDownCount));
    }

    [Theory]
    [InlineData("click", "garbage")]
    [InlineData("right-click", "garbage")]
    [InlineData("double-click", "garbage")]
    [InlineData("click", "1")]
    [InlineData("click", "yes")]
    [InlineData("click", "")]
    public void PointerVerbs_RejectNonBooleanOutsideWindowFlag(string verb, string value)
    {
        var result = UiCli.Run(verb, "--hwnd", "not-a-window", "--x", "10", "--y", "20",
            $"--allowOutsideWindow={value}");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--allowOutsideWindow must be true or false", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("--allowOutsideWindow")]
    [InlineData("--allowOutsideWindow=true")]
    [InlineData("--allowOutsideWindow=false")]
    public void PointerVerbs_AcceptBooleanOutsideWindowFlag(string option)
    {
        var result = UiCli.Run("click", "--hwnd", "not-a-window", "--x", "10", "--y", "20", option);

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("Invalid hwnd", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("click", false, 1, 0, 0)]
    [InlineData("right-click", false, 0, 1, 0)]
    [InlineData("double-click", false, 2, 0, 1)]
    [InlineData("click", true, 1, 0, 0)]
    [InlineData("right-click", true, 0, 1, 0)]
    [InlineData("double-click", true, 2, 0, 1)]
    [Trait("Category", "InteractiveDesktop")]
    public void PointerVerbs_ClickInBoundsButtonAfterRestoringIfMinimized(
        string verb, bool minimize, int expectedClicks, int expectedRightClicks, int expectedDoubleClicks)
    {
        Button button = null!;
        var clickCount = 0;
        var rightClickCount = 0;
        var doubleClickCount = 0;
        using var window = FixtureWindow.Create(() =>
        {
            button = new Button { Content = "Target", Width = 180, Height = 100 };
            button.Click += (_, _) => clickCount++;
            button.PreviewMouseRightButtonUp += (_, _) => rightClickCount++;
            button.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (args.ClickCount == 2)
                {
                    doubleClickCount++;
                }
            };
            return button;
        });

        var point = window.Invoke(() => button.PointToScreen(
            new Point(button.ActualWidth / 2, button.ActualHeight / 2)));
        var x = (int)point.X;
        var y = (int)point.Y;
        Assert.Equal(0, window.Invoke(() => clickCount + rightClickCount));
        if (minimize)
        {
            window.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Minimized);
            Assert.Equal(WindowState.Minimized, window.Invoke(() => Window.GetWindow(button).WindowState));
            Assert.True(SpinWait.SpinUntil(
                () => NativeMethods.IsIconic(window.Hwnd),
                TimeSpan.FromSeconds(2)), "The fixture window did not reach the native minimized state.");
        }

        var result = UiCli.Run(verb, "--hwnd", window.HwndArgument,
            "--x", x.ToString(CultureInfo.InvariantCulture), "--y", y.ToString(CultureInfo.InvariantCulture));

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"synthetic-{verb}", result.Json.GetProperty("method").GetString());
        Assert.Equal(x, result.Json.GetProperty("x").GetInt32());
        Assert.Equal(y, result.Json.GetProperty("y").GetInt32());
        Assert.True(SpinWait.SpinUntil(
            () => window.Invoke(() => clickCount + rightClickCount) == expectedClicks + expectedRightClicks,
            TimeSpan.FromSeconds(5)), "The fixture button did not receive the expected mouse clicks.");
        Assert.Equal(expectedClicks, window.Invoke(() => clickCount));
        Assert.Equal(expectedRightClicks, window.Invoke(() => rightClickCount));
        Assert.Equal(expectedDoubleClicks, window.Invoke(() => doubleClickCount));
        Assert.Equal(WindowState.Normal, window.Invoke(() => Window.GetWindow(button).WindowState));
    }

    [Fact]
    public void PointerVerbs_AllowOutsideFlagRequiresCoordinateTarget()
    {
        var result = UiCli.Run("click", "--hwnd", "0x1", "--strategy", "Name", "--value", "Target",
            "--allowOutsideWindow");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("only valid with the coordinate form", result.Json.GetProperty("message").GetString());
    }
}

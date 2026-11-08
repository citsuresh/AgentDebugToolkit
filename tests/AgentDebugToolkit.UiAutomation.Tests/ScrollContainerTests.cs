using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ScrollContainerTests
{
    private const string ContainerName = "Scroll fixture";

    [Fact]
    public void ScrollContainer_AlreadyAtTopReportsBoundaryWithoutChange()
    {
        ScrollViewer? viewer = null;
        using var window = CreateScrollableFixture(element => viewer = element);

        var result = UiCli.Run("scroll-container", "--hwnd", window.HwndArgument,
            "--strategy", "Name", "--value", ContainerName, "--verticalPercent", "0");

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(0, result.Json.GetProperty("verticalScrollPercentBefore").GetDouble());
        Assert.Equal(0, result.Json.GetProperty("verticalScrollPercentAfter").GetDouble());
        Assert.True(result.Json.GetProperty("boundaryReached").GetBoolean());
        Assert.False(result.Json.GetProperty("changed").GetBoolean());
        Assert.Equal(0, window.Invoke(() => viewer!.VerticalOffset));
    }

    [Fact]
    public void ScrollContainer_SetsBottomAndReportsChangedPosition()
    {
        ScrollViewer? viewer = null;
        using var window = CreateScrollableFixture(element => viewer = element);

        var result = UiCli.Run("scroll-container", "--hwnd", window.HwndArgument,
            "--strategy", "Name", "--value", ContainerName, "--verticalPercent", "100");

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(100, result.Json.GetProperty("verticalScrollPercentAfter").GetDouble());
        Assert.True(result.Json.GetProperty("boundaryReached").GetBoolean());
        Assert.True(result.Json.GetProperty("changed").GetBoolean());
        Assert.True(Poll.Until(() => window.Invoke(() => viewer!.VerticalOffset > 0), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ScrollContainer_NonScrollableControlReturnsNotScrollable()
    {
        using var window = FixtureWindow.Create(() =>
        {
            var button = new Button { Content = "Plain control" };
            AutomationProperties.SetName(button, "Non-scrollable fixture");
            return button;
        });

        var result = UiCli.Run("scroll-container", "--hwnd", window.HwndArgument,
            "--strategy", "Name", "--value", "Non-scrollable fixture", "--verticalPercent", "50");

        Assert.Equal("not-scrollable", result.ErrorCode);
    }

    [Fact]
    public void ScrollContainer_ClosedWindowHandleReturnsWindowGone()
    {
        var window = CreateScrollableFixture();
        var staleHwnd = window.HwndArgument;
        window.Dispose();

        var result = UiCli.Run("scroll-container", "--hwnd", staleHwnd,
            "--strategy", "Name", "--value", ContainerName, "--verticalPercent", "50");

        Assert.Equal("window-gone", result.ErrorCode);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100.1")]
    [InlineData("NaN")]
    public void ScrollContainer_RejectsPercentOutsideFiniteRange(string value)
    {
        var result = UiCli.Run("scroll-container", "--hwnd", "0x1",
            "--strategy", "Name", "--value", ContainerName, "--verticalPercent", value);

        Assert.Equal("invalid-argument", result.ErrorCode);
    }

    private static FixtureWindow CreateScrollableFixture(Action<ScrollViewer>? onCreated = null)
    {
        return FixtureWindow.Create(() =>
        {
            var content = new StackPanel();
            for (var index = 0; index < 30; index++)
            {
                content.Children.Add(new Border
                {
                    Height = 80,
                    Background = Brushes.LightGray,
                    Child = new TextBlock { Text = $"Row {index}" },
                });
            }

            var viewer = new ScrollViewer
            {
                Width = 380,
                Height = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Content = content,
            };
            AutomationProperties.SetName(viewer, ContainerName);
            onCreated?.Invoke(viewer);
            return viewer;
        });
    }
}

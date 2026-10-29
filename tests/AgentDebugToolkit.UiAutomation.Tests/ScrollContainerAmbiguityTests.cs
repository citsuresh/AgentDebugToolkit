using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ScrollContainerAmbiguityTests
{
    [Fact]
    public void ScrollContainer_RejectsMultipleMatchingContainers()
    {
        using var window = FixtureWindow.Create(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(CreateScrollViewer());
            panel.Children.Add(CreateScrollViewer());
            return panel;
        });

        var result = UiCli.Run("scroll-container", "--hwnd", window.HwndArgument,
            "--strategy", "Name", "--value", "Repeated scroll fixture", "--verticalPercent", "50");

        Assert.Equal("ambiguous-container", result.ErrorCode);
        Assert.Equal(2, result.Json.GetProperty("count").GetInt32());
    }

    private static ScrollViewer CreateScrollViewer()
    {
        var content = new StackPanel();
        for (var index = 0; index < 20; index++)
        {
            content.Children.Add(new Border
            {
                Height = 50,
                Background = Brushes.LightGray,
                Child = new TextBlock { Text = $"Row {index}" },
            });
        }

        var viewer = new ScrollViewer
        {
            Width = 380,
            Height = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Content = content,
        };
        AutomationProperties.SetName(viewer, "Repeated scroll fixture");
        return viewer;
    }
}

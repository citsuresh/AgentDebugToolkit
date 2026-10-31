using System.Windows.Automation;
using System.Windows.Controls;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ControlTypeSelectorTests
{
    private const string SharedName = "Shared selector";

    [Fact]
    public void FindAll_ControlTypeFiltersDuplicateNamesBeforeReturningMatches()
    {
        using var window = CreateMixedFixture();

        var all = UiCli.Run("find-all", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName);
        var buttons = UiCli.Run("find-all", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName,
            "--controlType", "Button");

        Assert.True(all.Success, all.RawOutput);
        Assert.Equal(2, all.Json.GetProperty("count").GetInt32());
        Assert.True(buttons.Success, buttons.RawOutput);
        Assert.Equal(1, buttons.Json.GetProperty("count").GetInt32());
        Assert.Equal("ControlType.Button",
            buttons.Json.GetProperty("elements")[0].GetProperty("controlType").GetString());
    }

    [Fact]
    public void GetText_ControlTypeDisambiguatesNamesAcrossControlTypes()
    {
        using var window = CreateMixedFixture();

        var ambiguous = UiCli.Run("get-text", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName);
        var selected = UiCli.Run("get-text", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName,
            "--controlType", "Button");

        Assert.Equal("ambiguous-match", ambiguous.ErrorCode);
        Assert.True(selected.Success, selected.RawOutput);
        Assert.Equal(SharedName, selected.Json.GetProperty("text").GetString());
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void Click_ControlTypeDisambiguatesDifferentTypesButNotRepeatedSameTypeMatches()
    {
        var clickCount = 0;
        using (var window = CreateMixedFixture(() => clickCount++))
        {
            var ambiguous = UiCli.Run("click", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName);
            Assert.Equal("ambiguous-match", ambiguous.ErrorCode);

            var selected = UiCli.Run("click", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName,
                "--controlType", "Button");
            if (selected.ErrorCode == "foreground-activation-failed")
            {
                Assert.False(selected.Success, selected.RawOutput);
                Assert.Equal("foreground-activation-failed", selected.ErrorCode);
                Assert.Equal(0, window.Invoke(() => clickCount));
            }
            else
            {
                Assert.True(selected.Success, selected.RawOutput);
                Assert.Equal("ControlType.Button", selected.Json.GetProperty("elementFound").GetProperty("ControlType").GetString());
                Assert.Equal(1, window.Invoke(() => clickCount));
            }
        }

        using var duplicateButtons = CreateButtonFixture(2);
        var sameTypeAmbiguous = UiCli.Run("click", "--hwnd", duplicateButtons.HwndArgument,
            "--strategy", "Name", "--value", SharedName, "--controlType", "Button");
        Assert.Equal("ambiguous-match", sameTypeAmbiguous.ErrorCode);
        Assert.Equal(2, sameTypeAmbiguous.Json.GetProperty("count").GetInt32());
    }

    [Fact]
    public void FindFirstAndWaitForElementApplyControlTypeFilter()
    {
        using var window = CreateMixedFixture();

        var first = UiCli.Run("find-first", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName,
            "--controlType", "Text");
        var waited = UiCli.Run("wait-for-element", "--hwnd", window.HwndArgument,
            "--strategy", "Name", "--value", SharedName, "--controlType", "Button", "--timeoutMs", "0");

        Assert.True(first.Success, first.RawOutput);
        Assert.True(first.Json.GetProperty("found").GetBoolean());
        Assert.Equal("ControlType.Text",
            first.Json.GetProperty("element").GetProperty("controlType").GetString());
        Assert.True(waited.Success, waited.RawOutput);
        Assert.Equal("ControlType.Button",
            waited.Json.GetProperty("elementFound").GetProperty("ControlType").GetString());
    }

    [Theory]
    [InlineData("click")]
    [InlineData("get-text")]
    [InlineData("find-first")]
    [InlineData("find-all")]
    [InlineData("wait-for-element")]
    public void SupportedSelectorVerbsRejectUnrecognizedControlType(string verb)
    {
        using var window = CreateMixedFixture();

        var result = UiCli.Run(verb, "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", SharedName,
            "--controlType", "NotAControlType");

        Assert.Equal("invalid-argument", result.ErrorCode);
    }

    private static FixtureWindow CreateMixedFixture(Action? onButtonClick = null)
    {
        return FixtureWindow.Create(() =>
        {
            var panel = new StackPanel();
            var button = new Button { Content = "Button" };
            if (onButtonClick is not null)
            {
                button.Click += (_, _) => onButtonClick();
            }
            var text = new TextBlock { Text = "Text" };
            AutomationProperties.SetName(button, SharedName);
            AutomationProperties.SetName(text, SharedName);

            panel.Children.Add(button);
            panel.Children.Add(text);
            return panel;
        });
    }

    private static FixtureWindow CreateButtonFixture(int count)
    {
        return FixtureWindow.Create(() =>
        {
            var panel = new StackPanel();
            for (var index = 0; index < count; index++)
            {
                var button = new Button { Content = $"Button {index}" };
                AutomationProperties.SetName(button, SharedName);
                panel.Children.Add(button);
            }

            return panel;
        });
    }
}

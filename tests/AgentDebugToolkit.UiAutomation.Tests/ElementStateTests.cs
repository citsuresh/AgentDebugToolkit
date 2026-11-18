using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using AgentDebugToolkit.Core;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ElementStateTests
{
    [Theory]
    [InlineData("on", true, true)]
    [InlineData("off", false, true)]
    [InlineData("indeterminate", false, false)]
    public void ReadState_ReadsOnlyPublishedValues(string toggle, bool selected, bool enabled)
    {
        var calls = new List<string>();
        var state = UiaHelper.ReadState(
            () => { calls.Add("enabled"); return enabled; },
            () => { calls.Add("toggle-pattern"); return () => { calls.Add("toggle-value"); return toggle; }; },
            () => { calls.Add("selection-pattern"); return () => { calls.Add("selection-value"); return selected; }; });

        Assert.Equal(enabled, state.IsEnabled);
        Assert.True(state.ToggleSupported);
        Assert.Equal(toggle, state.ToggleState);
        Assert.True(state.SelectionItemSupported);
        Assert.Equal(selected, state.IsSelected);
        Assert.Null(state.Errors);
        Assert.Equal(new[] { "enabled", "toggle-pattern", "toggle-value", "selection-pattern", "selection-value" }, calls);

        var (exitCode, json) = Write(state);
        Assert.Equal(0, exitCode);
        Assert.True(json.GetProperty("success").GetBoolean());
        var serialized = json.GetProperty("state");
        Assert.Equal(toggle, serialized.GetProperty("toggleState").GetString());
        Assert.Equal(selected, serialized.GetProperty("isSelected").GetBoolean());
        Assert.Equal(enabled, serialized.GetProperty("isEnabled").GetBoolean());
        Assert.False(serialized.TryGetProperty("errors", out _));
    }

    [Fact]
    public void UnsupportedPatterns_HaveExplicitSupportWithoutInventedState()
    {
        var state = UiaHelper.ReadState(() => false, () => null, () => null);
        var (exitCode, json) = Write(state);

        Assert.Equal(0, exitCode);
        Assert.True(json.GetProperty("success").GetBoolean());
        var serialized = json.GetProperty("state");
        Assert.False(serialized.GetProperty("isEnabled").GetBoolean());
        Assert.False(serialized.GetProperty("toggleSupported").GetBoolean());
        Assert.False(serialized.GetProperty("selectionItemSupported").GetBoolean());
        Assert.False(serialized.TryGetProperty("toggleState", out _));
        Assert.False(serialized.TryGetProperty("isSelected", out _));
        Assert.False(serialized.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("toggle-pattern")]
    [InlineData("toggle-value")]
    [InlineData("selection-pattern")]
    [InlineData("selection-value")]
    public void ProviderFailures_AreErrorsAndNeverBecomeUnsupportedOrFalse(string failure)
    {
        var state = UiaHelper.ReadState(
            () => failure == "enabled" ? throw new COMException("enabled failed") : true,
            () => failure == "toggle-pattern" ? throw new COMException("toggle probe failed")
                : () => failure == "toggle-value" ? throw new COMException("toggle read failed") : "on",
            () => failure == "selection-pattern" ? throw new COMException("selection probe failed")
                : () => failure == "selection-value" ? throw new COMException("selection read failed") : true);
        var (exitCode, json) = Write(state);

        Assert.Equal(1, exitCode);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("state-provider-error", json.GetProperty("error").GetString());
        var error = Assert.Single(state.Errors!);
        Assert.Equal("state-provider-error", error.Error);
        Assert.Contains("failed", error.Message);
        var serialized = json.GetProperty("state");
        switch (failure)
        {
            case "enabled":
                Assert.Equal("IsEnabled", error.Pattern);
                Assert.False(serialized.TryGetProperty("isEnabled", out _));
                break;
            case "toggle-pattern":
            case "toggle-value":
                Assert.Equal("TogglePattern", error.Pattern);
                Assert.False(serialized.TryGetProperty("toggleState", out _));
                if (failure == "toggle-pattern")
                    Assert.False(serialized.TryGetProperty("toggleSupported", out _));
                else
                    Assert.True(serialized.GetProperty("toggleSupported").GetBoolean());
                break;
            default:
                Assert.Equal("SelectionItemPattern", error.Pattern);
                Assert.False(serialized.TryGetProperty("isSelected", out _));
                if (failure == "selection-pattern")
                    Assert.False(serialized.TryGetProperty("selectionItemSupported", out _));
                else
                    Assert.True(serialized.GetProperty("selectionItemSupported").GetBoolean());
                break;
        }
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("argument")]
    [InlineData("not-supported")]
    public void ProviderValueExceptions_AreNotInterpretedAsUnsupported(string kind)
    {
        Exception exception = kind switch
        {
            "invalid-operation" => new InvalidOperationException("provider failure"),
            "argument" => new ArgumentException("provider failure"),
            _ => new NotSupportedException("provider failure")
        };
        var state = UiaHelper.ReadState(() => true, () => () => throw exception, () => null);

        Assert.True(state.ToggleSupported);
        Assert.Null(state.ToggleState);
        Assert.Equal("state-provider-error", Assert.Single(state.Errors!).Error);
        Assert.Equal(1, Write(state).ExitCode);
    }

    [Fact]
    public void StaleElement_TakesPrecedenceOverOtherProviderFailures()
    {
        var state = UiaHelper.ReadState(
            () => throw new COMException("enabled failed"),
            () => () => throw new ElementNotAvailableException("element disappeared"),
            () => null);
        var (exitCode, json) = Write(state);

        Assert.Equal(1, exitCode);
        Assert.Equal("stale-context", json.GetProperty("error").GetString());
        Assert.Equal("element disappeared", json.GetProperty("message").GetString());
        Assert.Null(state.IsEnabled);
        Assert.Null(state.ToggleState);
        Assert.True(state.ToggleSupported);
        Assert.Equal(2, state.Errors!.Count);
    }

    [Fact]
    public void UnrelatedExceptions_AreNotHiddenAsProviderFailures()
    {
        var exception = new ApplicationException("unrelated failure");
        Assert.Same(exception, Assert.Throws<ApplicationException>(() =>
            UiaHelper.ReadState(() => true, () => throw exception, () => null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootAcquisition_DefaultKeepsLegacyBehavior_OptInPropagates(bool stale)
    {
        Exception exception = stale ? new ElementNotAvailableException("unavailable")
            : new COMException("provider failure");
        var calls = 0;
        AutomationElement? Acquire(IntPtr hwnd)
        {
            Assert.Equal(new IntPtr(0x123), hwnd);
            calls++;
            throw exception;
        }

        Assert.Null(UiaHelper.FindWindowByHwnd("0x123", acquireWindow: Acquire));
        var actual = Record.Exception(() =>
            UiaHelper.FindWindowByHwnd("0x123", propagateErrors: true, acquireWindow: Acquire));
        Assert.Same(exception, actual);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void InspectionState_IsOptionalAndKeepsExistingPropertyNames()
    {
        var element = new ElementInfo { Name = "target", IsEnabled = false };
        var before = JsonSerializer.SerializeToElement(element, JsonOutput.Options);
        Assert.False(before.TryGetProperty("State", out _));
        element.State = UiaHelper.ReadState(() => false, () => null, () => null);
        var after = JsonSerializer.SerializeToElement(element, JsonOutput.Options);

        foreach (var property in before.EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), after.GetProperty(property.Name).GetRawText());
        }
        Assert.Equal(before.EnumerateObject().Count() + 1, after.EnumerateObject().Count());
        Assert.False(after.GetProperty("State").GetProperty("toggleSupported").GetBoolean());
    }

    private static (int ExitCode, JsonElement Json) Write(ElementStateInfo state)
    {
        using var output = new StringWriter();
        var original = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(output);
            exitCode = Verbs.WriteStateResult(state);
        }
        finally
        {
            Console.SetOut(original);
        }
        using var json = JsonDocument.Parse(output.ToString());
        return (exitCode, json.RootElement.Clone());
    }
}
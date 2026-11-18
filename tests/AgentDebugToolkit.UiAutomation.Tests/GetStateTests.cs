using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using AgentDebugToolkit.Mcp;
using AgentDebugToolkit.UiAutomation.Cli;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;
using CliResult = AgentDebugToolkit.UiAutomation.Tests.Infrastructure.CliResult;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class GetStateTests
{
    [Theory]
    [InlineData(ToggleState.On, true, true, "on")]
    [InlineData(ToggleState.Off, false, true, "off")]
    [InlineData(ToggleState.Indeterminate, false, false, "indeterminate")]
    public void GetState_AndInspectReadStateWithoutMutations(
        ToggleState toggle, bool selected, bool enabled, string expected)
    {
        StateProbe? probe = null;
        using var window = FixtureWindow.Create(() => probe = new StateProbe(toggle, selected) { IsEnabled = enabled });

        var result = Get(window);
        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(0, result.ExitCode);
        AssertState(result.Json.GetProperty("state"), expected, selected, enabled);

        var inspected = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "5",
            "--nameContains", "State target");
        Assert.True(inspected.Success, inspected.RawOutput);
        Assert.Equal(1, inspected.Json.GetProperty("matchCount").GetInt32());
        var element = FindProbe(inspected.Json.GetProperty("root"));
        AssertState(element.GetProperty("State"), expected, selected, enabled);
        Assert.Equal(enabled, element.GetProperty("IsEnabled").GetBoolean());

        window.Invoke(() =>
        {
            Assert.Equal(toggle, probe!.ToggleValue);
            Assert.Equal(selected, probe.Selected);
            Assert.Equal(0, probe.Mutations);
        });
    }

    [Fact]
    public void GetState_UnsupportedPatternsOmitValues()
    {
        StateProbe? probe = null;
        using var window = FixtureWindow.Create(() => probe = new StateProbe(ToggleState.On, true)
        {
            SupportsToggle = false,
            SupportsSelection = false
        });

        var result = Get(window);
        Assert.True(result.Success, result.RawOutput);
        var state = result.Json.GetProperty("state");
        Assert.False(state.GetProperty("toggleSupported").GetBoolean());
        Assert.False(state.GetProperty("selectionItemSupported").GetBoolean());
        Assert.False(state.TryGetProperty("toggleState", out _));
        Assert.False(state.TryGetProperty("isSelected", out _));
        Assert.False(state.TryGetProperty("errors", out _));
        Assert.Equal(0, window.Invoke(() => probe!.Mutations));
    }

    [Fact]
    public void GetState_ProviderReadFailureIsNotUnsupported_AndInspectRecordsIt()
    {
        StateProbe? probe = null;
        using var window = FixtureWindow.Create(() => probe = new StateProbe(ToggleState.On, true)
        {
            FailToggleRead = true
        });

        var result = Get(window);
        Assert.False(result.Success, result.RawOutput);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("state-provider-error", result.ErrorCode);
        var state = result.Json.GetProperty("state");
        Assert.True(state.GetProperty("toggleSupported").GetBoolean());
        Assert.False(state.TryGetProperty("toggleState", out _));
        Assert.True(state.GetProperty("isSelected").GetBoolean());
        Assert.Equal("TogglePattern", state.GetProperty("errors")[0].GetProperty("pattern").GetString());

        var inspected = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "5");
        Assert.True(inspected.Success, inspected.RawOutput);
        var inspectedState = FindProbe(inspected.Json.GetProperty("root")).GetProperty("State");
        Assert.Equal("state-provider-error", inspectedState.GetProperty("errors")[0].GetProperty("error").GetString());
        Assert.False(inspectedState.TryGetProperty("toggleState", out _));
        Assert.Equal(0, window.Invoke(() => probe!.Mutations));
    }

    [Fact]
    public void GetState_ControlTypeDisambiguatesButRepeatedSameTypeDoesNot()
    {
        var probes = new List<StateProbe>();
        using var window = FixtureWindow.Create(() =>
        {
            var panel = new StackPanel();
            var probe = new StateProbe(ToggleState.Off, false);
            probes.Add(probe);
            var text = new TextBlock { Text = "label" };
            AutomationProperties.SetName(text, "State target");
            panel.Children.Add(probe);
            panel.Children.Add(text);
            return panel;
        });

        var ambiguous = Get(window);
        Assert.Equal("ambiguous-match", ambiguous.ErrorCode);
        Assert.Equal(2, ambiguous.Json.GetProperty("count").GetInt32());
        Assert.Equal(2, ambiguous.Json.GetProperty("candidates").GetArrayLength());
        var narrowed = Get(window, "--controlType", "CheckBox");
        Assert.True(narrowed.Success, narrowed.RawOutput);
        Assert.Equal("off", narrowed.Json.GetProperty("state").GetProperty("toggleState").GetString());

        window.Invoke(() =>
        {
            var panel = (StackPanel)probes[0].Parent;
            var second = new StateProbe(ToggleState.On, true);
            probes.Add(second);
            panel.Children.Add(second);
        });
        var stillAmbiguous = Get(window, "--controlType", "CheckBox");
        Assert.Equal("ambiguous-match", stillAmbiguous.ErrorCode);
        Assert.Equal(2, stillAmbiguous.Json.GetProperty("count").GetInt32());
        Assert.All(probes, probe => Assert.Equal(0, window.Invoke(() => probe.Mutations)));
    }

    [Theory]
    [InlineData("--strategy", "NameRegex")]
    [InlineData("--controlType", "NotAControlType")]
    [InlineData("--scopeHwnd", "not-a-hwnd")]
    [InlineData("--scopeHwnd", "")]
    [InlineData("--scopeHwnd", "0x")]
    [InlineData("--scopeHwnd", " ")]
    [InlineData("--hwnd", "")]
    [InlineData("--hwnd", "0x")]
    [InlineData("--hwnd", " ")]
    [InlineData("--hwnd", "not-a-hwnd")]
    public void GetState_RejectsInvalidArgumentsBeforeAccessingTarget(string flag, string value)
    {
        var result = UiCli.Run("get-state", "--hwnd", "0", "--strategy", "Name", "--value", "State target", flag, value);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.ErrorCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GetState_RootAcquisitionErrorsRemainDistinct(bool scoped, bool stale)
    {
        using var primary = FixtureWindow.Create(() => new TextBlock { Text = "primary" });
        using var scope = FixtureWindow.Create(() => new TextBlock { Text = "scope" });
        var options = new Dictionary<string, string>
        {
            ["hwnd"] = primary.HwndArgument,
            ["strategy"] = "Name",
            ["value"] = "Target"
        };
        if (scoped)
        {
            options["scopeHwnd"] = scope.HwndArgument;
        }

        var calls = 0;
        using var output = new StringWriter();
        var original = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(output);
            exitCode = Verbs.GetState(options, hwnd =>
            {
                calls++;
                Assert.Equal(scoped ? scope.Hwnd : primary.Hwnd, hwnd);
                throw stale ? new ElementNotAvailableException("root disappeared")
                    : new COMException("root provider failed");
            });
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(1, calls);
        Assert.Equal(1, exitCode);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(stale ? "stale-context" : "state-provider-error",
            response.RootElement.GetProperty("error").GetString());
        Assert.Equal(stale ? "root disappeared" : "root provider failed",
            response.RootElement.GetProperty("message").GetString());
        Assert.False(response.RootElement.TryGetProperty("state", out _));
    }

    [Theory]
    [InlineData("--strategy", "Name")]
    [InlineData("--value", "target")]
    public void GetState_RequiresBothSelectorArguments(string flag, string value)
    {
        var result = UiCli.Run("get-state", "--hwnd", "0", flag, value);
        Assert.Equal("invalid-argument", result.ErrorCode);
    }

    [Fact]
    public async Task McpGetState_ForwardsSelectorControlTypeScopeAndFlagLikeValues()
    {
        using var primary = FixtureWindow.Create(() => new StateProbe(ToggleState.On, true));
        StateProbe? probe = null;
        using var scope = FixtureWindow.Create(() =>
        {
            probe = new StateProbe(ToggleState.Off, false);
            AutomationProperties.SetAutomationId(probe, "--state-target");
            return probe;
        });
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));

        var result = await tools.GetState(primary.HwndArgument, "AutomationId", "--state-target", "CheckBox", scope.HwndArgument);

        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Result);
        Assert.True(result.Result.Value.GetProperty("success").GetBoolean());
        AssertState(result.Result.Value.GetProperty("state"), "off", false, true);
        Assert.Equal(0, scope.Invoke(() => probe!.Mutations));

        var invalidType = await tools.GetState(primary.HwndArgument, "Name", "State target", "InvalidType");
        Assert.Null(invalidType.WrapperErrorCode);
        Assert.Equal("invalid-argument", invalidType.Result!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public void McpGetState_HasReadOnlyAnnotationsAndExpectedParameters()
    {
        var method = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.GetState))!;
        var annotation = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("ui_get_state", annotation.Name);
        Assert.True(annotation.ReadOnly);
        Assert.False(annotation.Destructive);
        Assert.False(annotation.OpenWorld);
        Assert.Equal(new[] { "hwnd", "strategy", "value", "controlType", "scopeHwnd", "cancellationToken" },
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.All(method.GetParameters().Take(3), parameter => Assert.False(parameter.IsOptional));
        Assert.All(method.GetParameters().Skip(3), parameter => Assert.True(parameter.IsOptional));
        Assert.Contains("Never invokes", method.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    private static CliResult Get(FixtureWindow window, params string[] extra) => UiCli.Run(
        new[] { "get-state", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", "State target" }
            .Concat(extra).ToArray());

    private static void AssertState(JsonElement state, string toggle, bool selected, bool enabled)
    {
        Assert.True(state.GetProperty("toggleSupported").GetBoolean());
        Assert.Equal(toggle, state.GetProperty("toggleState").GetString());
        Assert.True(state.GetProperty("selectionItemSupported").GetBoolean());
        Assert.Equal(selected, state.GetProperty("isSelected").GetBoolean());
        Assert.Equal(enabled, state.GetProperty("isEnabled").GetBoolean());
        Assert.False(state.TryGetProperty("errors", out _));
    }

    private static JsonElement FindProbe(JsonElement element)
    {
        if (element.GetProperty("AutomationId").GetString() == "state-target")
            return element;
        foreach (var child in element.GetProperty("Children").EnumerateArray())
        {
            var found = FindProbe(child);
            if (found.ValueKind != JsonValueKind.Undefined)
                return found;
        }
        return default;
    }

    private sealed class StateProbe : FrameworkElement
    {
        public StateProbe(ToggleState toggle, bool selected)
        {
            ToggleValue = toggle;
            Selected = selected;
            Width = 100;
            Height = 30;
            AutomationProperties.SetName(this, "State target");
            AutomationProperties.SetAutomationId(this, "state-target");
        }

        public ToggleState ToggleValue { get; }
        public bool Selected { get; }
        public bool SupportsToggle { get; init; } = true;
        public bool SupportsSelection { get; init; } = true;
        public bool FailToggleRead { get; init; }
        public int Mutations { get; set; }
        protected override AutomationPeer OnCreateAutomationPeer() => new StateProbePeer(this);
    }

    private sealed class StateProbePeer(StateProbe owner) : FrameworkElementAutomationPeer(owner),
        IToggleProvider, ISelectionItemProvider, IInvokeProvider, IScrollItemProvider
    {
        protected override string GetClassNameCore() => "StateProbe";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;
        protected override void SetFocusCore() => owner.Mutations++;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Toggle when owner.SupportsToggle => this,
            PatternInterface.SelectionItem when owner.SupportsSelection => this,
            PatternInterface.Invoke or PatternInterface.ScrollItem => this,
            _ => base.GetPattern(patternInterface)
        };

        public ToggleState ToggleState => owner.FailToggleRead
            ? throw new InvalidOperationException("state probe provider failure") : owner.ToggleValue;
        public bool IsSelected => owner.Selected;
        public IRawElementProviderSimple SelectionContainer => null!;
        public void Toggle() => owner.Mutations++;
        public void Select() => owner.Mutations++;
        public void AddToSelection() => owner.Mutations++;
        public void RemoveFromSelection() => owner.Mutations++;
        public void Invoke() => owner.Mutations++;
        public void ScrollIntoView() => owner.Mutations++;
    }
}
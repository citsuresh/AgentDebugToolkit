using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.Mcp;
using AgentDebugToolkit.UiAutomation.Cli;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;
using CliResult = AgentDebugToolkit.UiAutomation.Tests.Infrastructure.CliResult;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class SetToggleStateTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("off", "off", "off", false, null)]
    [InlineData("off", "on", "on", true, null)]
    [InlineData("off", "indeterminate", "on", true, "verify-mismatch")]
    [InlineData("on", "on", "on", false, null)]
    [InlineData("on", "off", "indeterminate", true, "verify-mismatch")]
    [InlineData("on", "indeterminate", "indeterminate", true, null)]
    [InlineData("indeterminate", "indeterminate", "indeterminate", false, null)]
    [InlineData("indeterminate", "off", "off", true, null)]
    [InlineData("indeterminate", "on", "off", true, "verify-mismatch")]
    public void GuardedToggle_UsesZeroOrOneCallAndVerifiesAllThreeStates(
        string before, string desired, string after, bool attempted, string? error)
    {
        var calls = 0;
        var reads = 0;
        var result = UiaHelper.SetToggleState(desired, before,
            () => { reads++; return State(calls == 0 ? before : after); }, () => calls++);

        Assert.Equal(attempted ? 1 : 0, calls);
        Assert.Equal(2, reads);
        Assert.Equal(attempted, result.MutationAttempted);
        Assert.Equal(error, result.Error);
        Assert.Equal(before, result.Before!.ToggleState);
        Assert.Equal(after, result.After!.ToggleState);
        var response = Write(desired, before, result);
        Assert.Equal(error is null, response.Success);
        Assert.Equal(error is null ? 0 : 1, response.ExitCode);
        Assert.Equal(before, response.Json.GetProperty("before").GetProperty("toggleState").GetString());
        Assert.Equal(after, response.Json.GetProperty("after").GetProperty("toggleState").GetString());
        Assert.Equal(attempted, response.Json.GetProperty("mutationAttempted").GetBoolean());
    }

    [Theory]
    [InlineData("on", "off")]
    [InlineData("off", "on")]
    [InlineData("indeterminate", "on")]
    public void StaleExpectedState_RefusesEvenAnAlreadyDesiredState(string current, string expected)
    {
        var calls = 0;
        var reads = 0;
        var result = UiaHelper.SetToggleState(current, expected,
            () => { reads++; return State(current); }, () => calls++);
        Assert.Equal("precondition-mismatch", result.Error);
        Assert.False(result.MutationAttempted);
        Assert.Equal(0, calls);
        Assert.Equal(1, reads);
        Assert.Equal(current, result.Before!.ToggleState);
        Assert.Null(result.After);
    }

    [Theory]
    [InlineData("unsupported", "toggle-not-supported")]
    [InlineData("unknown-support", "state-provider-error")]
    [InlineData("unknown-state", "state-provider-error")]
    [InlineData("unknown-enabled", "state-provider-error")]
    [InlineData("disabled", "element-disabled")]
    [InlineData("provider", "state-provider-error")]
    [InlineData("stale", "stale-context")]
    public void UnusableBeforeState_NeverMutates(string fault, string error)
    {
        var calls = 0;
        var result = UiaHelper.SetToggleState("on", "off", () => Fault(fault), () => calls++);
        Assert.Equal(error, result.Error);
        Assert.False(result.MutationAttempted);
        Assert.NotNull(result.Before);
        Assert.Null(result.After);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("unsupported", "state-provider-error")]
    [InlineData("unknown-support", "state-provider-error")]
    [InlineData("unknown-state", "state-provider-error")]
    [InlineData("unknown-enabled", "state-provider-error")]
    [InlineData("provider", "state-provider-error")]
    [InlineData("stale", "stale-context")]
    public void UnverifiableOrWrongAfterState_PreservesObservationsAndNeverRetries(string fault, string error)
    {
        var calls = 0;
        var reads = 0;
        var result = UiaHelper.SetToggleState("on", "off",
            () => ++reads == 1 ? State("off") : Fault(fault), () => calls++);
        Assert.Equal(error, result.Error);
        Assert.True(result.MutationAttempted);
        Assert.Equal(1, calls);
        Assert.Equal(2, reads);
        Assert.Equal("off", result.Before!.ToggleState);
        Assert.NotNull(result.After);
        var response = Write("on", "off", result);
        Assert.False(response.Success);
        Assert.Equal(1, response.ExitCode);
        Assert.True(response.Json.GetProperty("mutationAttempted").GetBoolean());
        Assert.True(response.Json.TryGetProperty("before", out _));
        Assert.True(response.Json.TryGetProperty("after", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingToggle_IsAttemptedAndObservedButNeverBecomesVerifiedSuccess(bool stale)
    {
        var calls = 0;
        var result = UiaHelper.SetToggleState("on", "off", () => State(calls == 0 ? "off" : "on"), () =>
        {
            calls++;
            if (stale) throw new ElementNotAvailableException("gone during toggle");
            throw new COMException("provider failed after changing state");
        });
        Assert.Equal(stale ? "stale-context" : "state-provider-error", result.Error);
        Assert.Contains(stale ? "gone" : "provider failed", result.Message);
        Assert.True(result.MutationAttempted);
        Assert.Equal("off", result.Before!.ToggleState);
        Assert.Equal("on", result.After!.ToggleState);
        Assert.Equal(1, calls);
        Assert.False(Write("on", "off", result).Success);
    }

    [Fact]
    public void NoOp_StillVerifiesAndFailsIfStateChangesBetweenObservations()
    {
        var calls = 0;
        var reads = 0;
        var result = UiaHelper.SetToggleState("off", "off",
            () => State(++reads == 1 ? "off" : "on"), () => calls++);
        Assert.Equal("verify-mismatch", result.Error);
        Assert.False(result.MutationAttempted);
        Assert.Equal(0, calls);
        Assert.Equal("off", result.Before!.ToggleState);
        Assert.Equal("on", result.After!.ToggleState);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("on")]
    public void DisabledTargets_AreRefusedEvenForNoOp(string desired)
    {
        var calls = 0;
        var result = UiaHelper.SetToggleState(desired, "off", () => Fault("disabled"), () => calls++);
        Assert.Equal("element-disabled", result.Error);
        Assert.False(result.MutationAttempted);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void StaleAfterRead_TakesPrecedenceOverAnEarlierMutationProviderError()
    {
        var calls = 0;
        var result = UiaHelper.SetToggleState("on", "off", () => calls == 0 ? State("off") : Fault("stale"), () =>
        {
            calls++;
            throw new COMException("toggle failed");
        });
        Assert.Equal("stale-context", result.Error);
        Assert.True(result.MutationAttempted);
        Assert.Equal(1, calls);
        Assert.Equal("stale-context", Assert.Single(result.After!.Errors!).Error);
    }

    [Fact]
    public void TargetDisabledByTheToggle_CanStillHaveAVerifiedResult()
    {
        var calls = 0;
        var result = UiaHelper.SetToggleState("on", "off", () =>
        {
            var state = State(calls == 0 ? "off" : "on");
            state.IsEnabled = calls == 0;
            return state;
        }, () => calls++);
        Assert.Null(result.Error);
        Assert.True(result.MutationAttempted);
        Assert.False(result.After!.IsEnabled);
        Assert.Equal("on", result.After.ToggleState);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("", "off")]
    [InlineData("on", "unknown")]
    [InlineData("ON", "off")]
    public void InvalidHelperArguments_DoNotReadOrMutate(string desired, string expected)
    {
        var reads = 0;
        var calls = 0;
        var result = UiaHelper.SetToggleState(desired, expected,
            () => { reads++; return State("off"); }, () => calls++);
        Assert.Equal("invalid-argument", result.Error);
        Assert.False(result.MutationAttempted);
        Assert.Null(result.Before);
        Assert.Equal(0, reads);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(ToggleState.Off, "off", "on", true)]
    [InlineData(ToggleState.On, "on", "indeterminate", true)]
    [InlineData(ToggleState.Indeterminate, "indeterminate", "off", true)]
    [InlineData(ToggleState.Off, "off", "off", false)]
    [InlineData(ToggleState.On, "on", "on", false)]
    [InlineData(ToggleState.Indeterminate, "indeterminate", "indeterminate", false)]
    public void Cli_UsesOnlyTogglePatternAndVerifies(ToggleState initial, string expected, string desired, bool attempted)
    {
        var baselineFocus = attempted ? DirectToggleBaseline(() => new ToggleProbe(initial)) : Array.Empty<int>();
        ToggleProbe? probe = null;
        using var window = FixtureWindow.Create(() => probe = new ToggleProbe(initial));
        var result = Set(window, desired, expected);
        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(attempted, result.Json.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(expected, result.Json.GetProperty("before").GetProperty("toggleState").GetString());
        Assert.Equal(desired, result.Json.GetProperty("after").GetProperty("toggleState").GetString());
        Assert.Equal(desired, result.Json.GetProperty("desiredState").GetString());
        Assert.Equal(expected, result.Json.GetProperty("expectedState").GetString());
        Assert.False(result.Json.GetProperty("before").TryGetProperty("selectionItemSupported", out _));
        AssertCounts(window, probe!, attempted ? 1 : 0, baselineFocus);
    }

    [Theory]
    [InlineData("disabled", "element-disabled", 0)]
    [InlineData("unsupported", "toggle-not-supported", 0)]
    [InlineData("read-error", "state-provider-error", 0)]
    [InlineData("unknown", "state-provider-error", 0)]
    [InlineData("precondition", "precondition-mismatch", 0)]
    [InlineData("post-error", "state-provider-error", 1)]
    [InlineData("wrong-post", "verify-mismatch", 1)]
    [InlineData("throw-toggle", "state-provider-error", 1)]
    public void Cli_RefusalsAndPostFailuresDoNotUseFallbacks(string mode, string error, int calls)
    {
        ToggleProbe CreateProbe() => new(
            mode == "unknown" ? (ToggleState)99 : ToggleState.Off)
        {
            IsEnabled = mode != "disabled",
            SupportsToggle = mode != "unsupported",
            FailRead = mode == "read-error",
            FailPostRead = mode == "post-error",
            KeepState = mode == "wrong-post",
            ThrowOnToggle = mode == "throw-toggle"
        };
        var baselineFocus = calls == 1 ? DirectToggleBaseline(CreateProbe) : Array.Empty<int>();
        ToggleProbe? probe = null;
        using var window = FixtureWindow.Create(() => probe = CreateProbe());
        var result = Set(window, "on", mode == "precondition" ? "on" : "off");
        Assert.False(result.Success, result.RawOutput);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(error, result.ErrorCode);
        Assert.Equal(calls != 0, result.Json.GetProperty("mutationAttempted").GetBoolean());
        Assert.True(result.Json.TryGetProperty("before", out _));
        Assert.Equal(calls != 0, result.Json.TryGetProperty("after", out _));
        AssertCounts(window, probe!, calls, baselineFocus);
    }

    [Theory]
    [InlineData("desiredState", null)]
    [InlineData("expectedState", null)]
    [InlineData("desiredState", "")]
    [InlineData("expectedState", " ")]
    [InlineData("desiredState", "true")]
    [InlineData("desiredState", "ON")]
    [InlineData("expectedState", "any")]
    [InlineData("strategy", "NameRegex")]
    [InlineData("strategy", null)]
    [InlineData("value", null)]
    [InlineData("controlType", "NotAControlType")]
    [InlineData("hwnd", "")]
    [InlineData("hwnd", "0x")]
    [InlineData("hwnd", "not-a-hwnd")]
    [InlineData("scopeHwnd", "0x")]
    [InlineData("scopeHwnd", " ")]
    public void Cli_ValidatesArgumentsBeforeTargetAccess(string flag, string? value)
    {
        var options = new Dictionary<string, string>
        {
            ["hwnd"] = "0", ["strategy"] = "Name", ["value"] = "Toggle target",
            ["desiredState"] = "on", ["expectedState"] = "off"
        };
        if (value is null) options.Remove(flag);
        else options[flag] = value;
        var result = UiCli.Run(new[] { "set-toggle-state" }.Concat(
            options.Select(pair => $"--{pair.Key}={pair.Value}")).ToArray());
        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Json.GetProperty("mutationAttempted").GetBoolean());
    }

    [Fact]
    public void Cli_AmbiguityAndMissingTargetsNeverToggle_ControlTypeNarrows()
    {
        static UIElement BuildContent(ToggleProbe probe)
        {
            var panel = new StackPanel();
            panel.Children.Add(probe);
            var label = new TextBlock { Text = "label" };
            AutomationProperties.SetName(label, "Toggle target");
            panel.Children.Add(label);
            return panel;
        }
        var baselineFocus = DirectToggleBaseline(() => new ToggleProbe(ToggleState.Off), BuildContent);
        ToggleProbe? probe = null;
        using var window = FixtureWindow.Create(() => BuildContent(probe = new ToggleProbe(ToggleState.Off)));
        var missing = Set(window, "on", "off", "--value", "Missing");
        Assert.Equal("element-not-found", missing.ErrorCode);
        var ambiguous = Set(window, "on", "off");
        Assert.Equal("ambiguous-match", ambiguous.ErrorCode);
        Assert.Equal(2, ambiguous.Json.GetProperty("count").GetInt32());
        Assert.Equal(2, ambiguous.Json.GetProperty("candidates").GetArrayLength());
        AssertCounts(window, probe!, 0);
        var narrowed = Set(window, "on", "off", "--controlType", "CheckBox");
        Assert.True(narrowed.Success, narrowed.RawOutput);
        AssertCounts(window, probe!, 1, baselineFocus);
        window.Invoke(() => ((StackPanel)probe!.Parent).Children.Add(new ToggleProbe(ToggleState.Off)));
        Assert.Equal("ambiguous-match", Set(window, "off", "on", "--controlType", "CheckBox").ErrorCode);
        AssertCounts(window, probe!, 1, baselineFocus);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RootFailures_AreNotUnsupportedOrSuccessful(bool scoped, bool stale)
    {
        using var primary = FixtureWindow.Create(() => new TextBlock());
        using var scope = FixtureWindow.Create(() => new TextBlock());
        var options = new Dictionary<string, string>
        {
            ["hwnd"] = primary.HwndArgument, ["strategy"] = "Name", ["value"] = "Target",
            ["desiredState"] = "on", ["expectedState"] = "off"
        };
        if (scoped) options["scopeHwnd"] = scope.HwndArgument;
        using var output = new StringWriter();
        var original = Console.Out;
        var calls = 0;
        int exitCode;
        try
        {
            Console.SetOut(output);
            exitCode = Verbs.SetToggleState(options, hwnd =>
            {
                calls++;
                Assert.Equal(scoped ? scope.Hwnd : primary.Hwnd, hwnd);
                if (stale) throw new ElementNotAvailableException("root disappeared");
                throw new COMException("root provider failed");
            });
        }
        finally { Console.SetOut(original); }
        Assert.Equal(1, calls);
        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(stale ? "stale-context" : "state-provider-error", json.RootElement.GetProperty("error").GetString());
        Assert.False(json.RootElement.GetProperty("mutationAttempted").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("before", out _));
    }

    [Fact]
    public async Task Mcp_ForwardsRequiredStatesAndScopedFlagLikeSelectors()
    {
        ToggleProbe? primaryProbe = null;
        using var primary = FixtureWindow.Create(() => primaryProbe = new ToggleProbe(ToggleState.Off));
        static ToggleProbe CreateProbe()
        {
            var probe = new ToggleProbe(ToggleState.Off);
            AutomationProperties.SetAutomationId(probe, "--toggle-target");
            return probe;
        }
        var baselineFocus = DirectToggleBaseline(CreateProbe);
        ToggleProbe? probe = null;
        using var scope = FixtureWindow.Create(() => probe = CreateProbe());
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        var result = await tools.SetToggleState(primary.HwndArgument, "AutomationId", "--toggle-target",
            "on", "off", "CheckBox", scope.HwndArgument);
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal("on", result.Result.Value.GetProperty("desiredState").GetString());
        Assert.Equal("off", result.Result.Value.GetProperty("expectedState").GetString());
        AssertCounts(primary, primaryProbe!, 0);
        AssertCounts(scope, probe!, 1, baselineFocus);
        var repeated = await tools.SetToggleState(primary.HwndArgument, "AutomationId", "--toggle-target",
            "on", "off", "CheckBox", scope.HwndArgument);
        Assert.Equal(1, repeated.ExitCode);
        Assert.Equal("precondition-mismatch", repeated.Result!.Value.GetProperty("error").GetString());
        AssertCounts(scope, probe!, 1, baselineFocus);
        var invalid = await tools.SetToggleState(primary.HwndArgument, "Name", "Toggle target", "on", "");
        Assert.Equal("invalid-argument", invalid.Result!.Value.GetProperty("error").GetString());
        AssertCounts(primary, primaryProbe!, 0);
        AssertCounts(scope, probe!, 1, baselineFocus);
    }

    [Fact]
    public void Mcp_IsExplicitlyMutatingAndRequiresBothStates()
    {
        var method = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.SetToggleState))!;
        var annotation = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("ui_set_toggle_state", annotation.Name);
        Assert.False(annotation.ReadOnly);
        Assert.True(annotation.Destructive);
        Assert.False(annotation.Idempotent);
        Assert.False(annotation.OpenWorld);
        Assert.Equal(new[] { "hwnd", "strategy", "value", "desiredState", "expectedState", "controlType", "scopeHwnd", "cancellationToken" },
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.All(method.GetParameters().Take(5), parameter => Assert.False(parameter.IsOptional));
        Assert.All(method.GetParameters().Skip(5), parameter => Assert.True(parameter.IsOptional));
    }

    private static ElementStateInfo State(string value) => new() { IsEnabled = true, ToggleSupported = true, ToggleState = value };

    [Fact]
    public void DirectTogglePatternCall_RecordsTheSyntheticProviderFocusBaseline()
    {
        Assert.Equal(new[] { 0 }, DirectToggleBaseline(() => new ToggleProbe(ToggleState.Off)));
    }

    private int[] DirectToggleBaseline(Func<ToggleProbe> createProbe, Func<ToggleProbe, UIElement>? buildContent = null)
    {
        ToggleProbe? probe = null;
        using var window = FixtureWindow.Create(() =>
        {
            probe = createProbe();
            return buildContent is null ? probe : buildContent(probe);
        });
        var root = AutomationElement.FromHandle(window.Hwnd);
        var target = root.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.NameProperty, "Toggle target"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)));
        Assert.NotNull(target);
        Assert.True(target.Current.IsEnabled);
        Assert.True(target.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern));
        var toggle = Assert.IsType<TogglePattern>(pattern);
        Assert.Equal(window.Invoke(() => probe!.State), toggle.Current.ToggleState);
        AssertCounts(window, probe!, 0, context: "direct UIA baseline pre-read");

        var actionError = Record.Exception(toggle.Toggle);
        Assert.Equal(probe!.ThrowOnToggle, actionError is not null);
        if (actionError is not null)
            Assert.True(actionError is InvalidOperationException or COMException, actionError.ToString());
        Assert.True(target.Current.IsEnabled);
        Assert.True(target.TryGetCurrentPattern(TogglePattern.Pattern, out var afterPattern));
        var afterToggle = Assert.IsType<TogglePattern>(afterPattern);
        var readError = Record.Exception(() => { _ = afterToggle.Current.ToggleState; });
        Assert.Equal(probe.FailPostRead, readError is not null);
        if (readError is not null)
            Assert.True(readError is InvalidOperationException or COMException, readError.ToString());

        var focusRequests = window.Invoke(() => probe.FocusRequestToggleCounts.ToArray());
        // This is the observed baseline for this synthetic WPF provider, not a UIA guarantee.
        Assert.Equal(new[] { 0 }, focusRequests);
        AssertCounts(window, probe, 1, focusRequests, "direct UIA baseline");
        return focusRequests;
    }

    private static ElementStateInfo Fault(string fault)
    {
        var state = State("off");
        switch (fault)
        {
            case "unsupported": state.ToggleSupported = false; state.ToggleState = null; break;
            case "unknown-support": state.ToggleSupported = null; break;
            case "unknown-state": state.ToggleState = "unknown"; break;
            case "unknown-enabled": state.IsEnabled = null; break;
            case "disabled": state.IsEnabled = false; break;
            default:
                state.ToggleState = null;
                state.Errors = new() { new("TogglePattern", fault == "stale" ? "stale-context" : "state-provider-error", "read failed") };
                break;
        }
        return state;
    }

    private static CliResult Write(string desired, string expected, UiaHelper.ToggleStateChangeResult result)
    {
        using var output = new StringWriter();
        var original = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(output);
            exitCode = Verbs.WriteToggleStateResult(desired, expected, result);
        }
        finally { Console.SetOut(original); }
        using var document = JsonDocument.Parse(output.ToString());
        return new CliResult(exitCode, document.RootElement.Clone(), output.ToString());
    }

    private static CliResult Set(FixtureWindow window, string desired, string expected, params string[] extra) => UiCli.Run(
        new[] { "set-toggle-state", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", "Toggle target",
            "--desiredState", desired, "--expectedState", expected }.Concat(extra).ToArray());

    private void AssertCounts(FixtureWindow window, ToggleProbe probe, int toggles,
        int[]? baselineFocus = null, string context = "CLI/MCP") => window.Invoke(() =>
    {
        var expectedFocus = baselineFocus ?? Array.Empty<int>();
        output.WriteLine($"{context}: toggles={probe.ToggleCalls}; focus requests={probe.FocusRequestToggleCounts.Count}; " +
            $"expected focus requests={expectedFocus.Length}; request toggle counts=[{string.Join(",", probe.FocusRequestToggleCounts)}]");
        Assert.Equal(toggles, probe.ToggleCalls);
        if (!expectedFocus.SequenceEqual(probe.FocusRequestToggleCounts))
            output.WriteLine(string.Join(Environment.NewLine, probe.FocusRequestStacks));
        Assert.Equal<int>(expectedFocus, probe.FocusRequestToggleCounts);
        Assert.True(probe.OtherMutations == 0, string.Join(Environment.NewLine, probe.OtherMutationCalls));
        Assert.Empty(probe.OtherMutationCalls);
        Assert.Equal(0, probe.OtherMutations);
        Assert.Equal(0, probe.SelectionReads);
    });

    private sealed class ToggleProbe : FrameworkElement
    {
        public ToggleProbe(ToggleState state)
        {
            State = state;
            Width = 100;
            Height = 30;
            AutomationProperties.SetName(this, "Toggle target");
        }
        public ToggleState State { get; set; }
        public bool SupportsToggle { get; init; } = true;
        public bool FailRead { get; init; }
        public bool FailPostRead { get; init; }
        public bool KeepState { get; init; }
        public bool ThrowOnToggle { get; init; }
        public int ToggleCalls { get; set; }
        public int OtherMutations { get; set; }
        public List<string> OtherMutationCalls { get; } = new();
        public List<int> FocusRequestToggleCounts { get; } = new();
        public List<string> FocusRequestStacks { get; } = new();
        public int SelectionReads { get; set; }
        public void RecordFocusRequest()
        {
            FocusRequestToggleCounts.Add(ToggleCalls);
            FocusRequestStacks.Add($"focus (after {ToggleCalls} toggles): {new System.Diagnostics.StackTrace()}");
        }
        public void RecordOtherMutation(string operation)
        {
            OtherMutations++;
            OtherMutationCalls.Add($"{operation} (after {ToggleCalls} toggles): {new System.Diagnostics.StackTrace()}");
        }
        protected override AutomationPeer OnCreateAutomationPeer() => new ToggleProbePeer(this);
    }

    private sealed class ToggleProbePeer(ToggleProbe owner) : FrameworkElementAutomationPeer(owner),
        IToggleProvider, IInvokeProvider, IScrollItemProvider, ISelectionItemProvider
    {
        protected override string GetClassNameCore() => "ToggleProbe";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;
        // Do not change keyboard focus: an extra request must not mask UIA's baseline request.
        protected override void SetFocusCore() => owner.RecordFocusRequest();
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Toggle when owner.SupportsToggle => this,
            PatternInterface.Invoke or PatternInterface.ScrollItem or PatternInterface.SelectionItem => this,
            _ => base.GetPattern(patternInterface)
        };
        public ToggleState ToggleState => owner.FailRead || (owner.FailPostRead && owner.ToggleCalls > 0)
            ? throw new InvalidOperationException("toggle state provider failed") : owner.State;
        public void Toggle()
        {
            owner.ToggleCalls++;
            if (!owner.KeepState)
                owner.State = owner.State switch { ToggleState.Off => ToggleState.On, ToggleState.On => ToggleState.Indeterminate, _ => ToggleState.Off };
            if (owner.ThrowOnToggle) throw new InvalidOperationException("toggle provider failed after mutation");
        }
        public bool IsSelected { get { owner.SelectionReads++; return false; } }
        public IRawElementProviderSimple SelectionContainer => null!;
        public void Invoke() => owner.RecordOtherMutation("invoke");
        public void ScrollIntoView() => owner.RecordOtherMutation("scroll-into-view");
        public void Select() => owner.RecordOtherMutation("select");
        public void AddToSelection() => owner.RecordOtherMutation("add-to-selection");
        public void RemoveFromSelection() => owner.RecordOtherMutation("remove-from-selection");
    }
}
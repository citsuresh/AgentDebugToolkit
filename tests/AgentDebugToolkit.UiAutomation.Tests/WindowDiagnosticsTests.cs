using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentDebugToolkit.Core;
using AgentDebugToolkit.Mcp;
using AgentDebugToolkit.UiAutomation.Cli;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class WindowDiagnosticsTests
{
    [Theory]
    [InlineData("find-first")]
    [InlineData("find-all")]
    [InlineData("inspect")]
    [InlineData("get-text")]
    [InlineData("get-state")]
    [InlineData("set-toggle-state")]
    [InlineData("wait-for-element")]
    [InlineData("scroll-container")]
    [InlineData("set-grid-cell")]
    [InlineData("submit-chat-message")]
    [Trait("Category", "InteractiveDesktop")]
    public void MissingSelectors_ReportActualMinimizedFactWithoutRestoringOrActivating(string verb)
    {
        Button button = null!;
        var clicks = 0;
        using var window = FixtureWindow.Create(() =>
        {
            button = new Button { Content = "Present" };
            button.Click += (_, _) => clicks++;
            return button;
        });
        window.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Minimized);
        Assert.True(NativeMethods.IsIconic(window.Hwnd));
        var foreground = NativeMethods.GetForegroundWindow();
        var options = new List<string> { verb, "--hwnd", window.HwndArgument };
        if (verb == "inspect") options.AddRange(new[] { "--nameContains", "Missing", "--maxDepth", "3" });
        else if (verb == "set-grid-cell")
            options.AddRange(new[] { "--gridStrategy", "Name", "--gridValue", "Missing", "--rowStrategy", "Name", "--rowValue", "Row", "--columnIndex", "0", "--text", "test" });
        else if (verb == "submit-chat-message") options.AddRange(new[] { "--inputAutomationId", "Missing", "--text", "test" });
        else options.AddRange(new[] { "--strategy", "Name", "--value", "Missing" });
        if (verb == "set-toggle-state") options.AddRange(new[] { "--desiredState", "on", "--expectedState", "off" });
        if (verb == "scroll-container") options.AddRange(new[] { "--verticalPercent", "50" });
        if (verb == "wait-for-element") options.AddRange(new[] { "--timeoutMs", "0" });
        var result = UiCli.Run(options.ToArray());
        if (verb is "find-first" or "find-all" or "inspect")
        {
            Assert.True(result.Success, result.RawOutput);
            Assert.Equal(0, result.ExitCode);
            if (verb == "find-first") Assert.False(result.Json.GetProperty("found").GetBoolean());
            else Assert.Equal(0, result.Json.GetProperty(verb == "find-all" ? "count" : "matchCount").GetInt32());
        }
        else
        {
            Assert.False(result.Success, result.RawOutput);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(verb == "wait-for-element" ? "timeout" : verb == "scroll-container" ? "container-not-found" : "element-not-found", result.ErrorCode);
        }
        var diagnostics = result.Json.GetProperty("lookupDiagnostics");
        Assert.Equal(window.HwndArgument, diagnostics.GetProperty("rootHwnd").GetString());
        Assert.True(diagnostics.GetProperty("isMinimized").GetBoolean());
        var hint = diagnostics.GetProperty("hint").GetString();
        Assert.Contains("--restore true", hint);
        Assert.Contains("If this is a WinForms tabbed UI", hint);
        Assert.Contains("check the selected tab", hint);
        Assert.True(NativeMethods.IsIconic(window.Hwnd));
        Assert.Equal(foreground, NativeMethods.GetForegroundWindow());
        Assert.Equal(0, window.Invoke(() => clicks));
    }

    [Fact]
    public void ScopedMissingSelector_UsesTheScopeWindowFactRatherThanThePrimaryWindow()
    {
        using var primary = FixtureWindow.Create(() => new Button());
        Button button = null!;
        using var scope = FixtureWindow.Create(() => button = new Button());
        scope.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Minimized);
        var result = UiCli.Run("get-text", "--hwnd", primary.HwndArgument, "--scopeHwnd", scope.HwndArgument,
            "--strategy", "Name", "--value", "Missing");
        Assert.Equal("element-not-found", result.ErrorCode);
        var diagnostics = result.Json.GetProperty("lookupDiagnostics");
        Assert.Equal(scope.HwndArgument, diagnostics.GetProperty("rootHwnd").GetString());
        Assert.True(diagnostics.GetProperty("isMinimized").GetBoolean());
        Assert.False(NativeMethods.IsIconic(primary.Hwnd));
        Assert.True(NativeMethods.IsIconic(scope.Hwnd));
    }

    [Fact]
    public void MissingTabContent_OnlyGetsConditionalGuidanceAndNeverSwitchesTabs()
    {
        TabControl tabs = null!;
        using var window = FixtureWindow.Create(() =>
        {
            tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "First", Content = new Button { Content = "Present" } });
            var hidden = new Button { Content = "Hidden" };
            AutomationProperties.SetName(hidden, "Hidden content");
            tabs.Items.Add(new TabItem { Header = "Second", Content = hidden });
            tabs.SelectedIndex = 0;
            return tabs;
        });
        var result = UiCli.Run("find-first", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", "Hidden content");
        Assert.True(result.Success, result.RawOutput);
        Assert.False(result.Json.GetProperty("found").GetBoolean());
        var diagnostics = result.Json.GetProperty("lookupDiagnostics");
        Assert.False(diagnostics.GetProperty("isMinimized").GetBoolean());
        var hint = diagnostics.GetProperty("hint").GetString();
        Assert.Contains("If this is a WinForms tabbed UI", hint);
        Assert.Contains("may not expose", hint);
        Assert.DoesNotContain("--restore true", hint);
        Assert.Equal(0, window.Invoke(() => tabs.SelectedIndex));
        window.Invoke(() => tabs.SelectedIndex = 1);
        var found = UiCli.Run("find-first", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", "Hidden content");
        Assert.True(found.Json.GetProperty("found").GetBoolean());
        Assert.False(found.Json.TryGetProperty("lookupDiagnostics", out _));
    }

    [Theory]
    [InlineData("find-first")]
    [InlineData("find-all")]
    [InlineData("inspect")]
    public void SuccessfulMatches_DoNotAddMissingSelectorDiagnostics(string verb)
    {
        using var window = FixtureWindow.Create(() => new Button { Content = "Present" });
        var options = new[] { verb, "--hwnd", window.HwndArgument };
        options = options.Concat(verb == "inspect" ? new[] { "--nameContains", "Present" }
            : new[] { "--strategy", "Name", "--value", "Present" }).ToArray();
        var result = UiCli.Run(options);
        Assert.True(result.Success, result.RawOutput);
        Assert.False(result.Json.TryGetProperty("lookupDiagnostics", out _));
    }

    [Fact]
    public void GoneWindow_DoesNotInventFalseMinimizedFacts()
    {
        var window = FixtureWindow.Create(() => new Button());
        var hwnd = window.Hwnd;
        window.Dispose();
        Assert.Null(Verbs.ReadMinimizedState(hwnd));
        var json = JsonSerializer.SerializeToElement(Verbs.CreateLookupDiagnostics(hwnd), JsonOutput.Options);
        Assert.False(json.TryGetProperty("isMinimized", out _));
        Assert.False(json.TryGetProperty("rootHwnd", out _));
        Assert.Contains("If this is a WinForms tabbed UI", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void MissingScopeSelector_RetainsElementNotFoundWithObservedFacts()
    {
        using var window = FixtureWindow.Create(() => new Button());
        var result = UiCli.Run("find-first", "--hwnd", window.HwndArgument,
            "--scopeStrategy", "Name", "--scopeValue", "Missing scope", "--strategy", "Name", "--value", "Missing");
        Assert.Equal("element-not-found", result.ErrorCode);
        Assert.False(result.Json.GetProperty("lookupDiagnostics").GetProperty("isMinimized").GetBoolean());
    }

    [Fact]
    public void Ambiguity_RetainsCandidatesAndDoesNotInventMissingSelectorHints()
    {
        using var window = FixtureWindow.Create(() => new StackPanel
        {
            Children = { new Button { Content = "Shared" }, new Button { Content = "Shared" } }
        });
        var result = UiCli.Run("get-text", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", "Shared",
            "--controlType", "Button");
        Assert.Equal("ambiguous-match", result.ErrorCode);
        Assert.Equal(2, result.Json.GetProperty("count").GetInt32());
        Assert.Equal(2, result.Json.GetProperty("candidates").GetArrayLength());
        Assert.False(result.Json.TryGetProperty("lookupDiagnostics", out _));
    }

    [Fact]
    public async Task Mcp_EmptyFindAllRelaysFactsWithoutRestoring()
    {
        Button button = null!;
        using var window = FixtureWindow.Create(() => button = new Button());
        window.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Minimized);
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        var result = await tools.FindAll(window.HwndArgument, "Name", "Missing");
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(0, result.ExitCode);
        var json = result.Result!.Value;
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.Empty(json.GetProperty("elements").EnumerateArray());
        Assert.True(json.GetProperty("lookupDiagnostics").GetProperty("isMinimized").GetBoolean());
        Assert.True(NativeMethods.IsIconic(window.Hwnd));
    }
}
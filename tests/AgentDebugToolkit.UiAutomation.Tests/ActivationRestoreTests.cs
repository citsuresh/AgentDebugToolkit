using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentDebugToolkit.Mcp;
using AgentDebugToolkit.UiAutomation.Cli;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ActivationRestoreTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, false, true, true, null)]
    [InlineData(false, false, false, true, null)]
    [InlineData(true, true, true, true, "stale-context")]
    [InlineData(true, false, true, false, "stale-context")]
    [InlineData(false, false, false, false, "stale-context")]
    [InlineData(false, true, false, true, "stale-context")]
    public void RestoreFlow_RequiresObservedNonMinimizedStateAndVerifiedForeground(
        bool before, bool after, bool restoreAttempted, bool foregroundVerified, string? error)
    {
        var reads = 0;
        var activations = 0;
        var result = Verbs.ActivateRestoringWindow(() => ++reads == 1 ? before : after, attempted =>
        {
            activations++;
            if (restoreAttempted) attempted();
            return foregroundVerified;
        });
        Assert.Equal(2, reads);
        Assert.Equal(1, activations);
        Assert.Equal(error, result.Error);
        Assert.Equal(error is null, result.Activated);
        Assert.Equal(restoreAttempted, result.RestoreAttempted);
        Assert.Equal(before, result.IsMinimizedBefore);
        Assert.Equal(after, result.IsMinimizedAfter);
        var (exitCode, json) = Write(result);
        Assert.Equal(error is null ? 0 : 1, exitCode);
        Assert.Equal(error is null, json.GetProperty("success").GetBoolean());
        Assert.Equal(error is null, json.GetProperty("activated").GetBoolean());
        Assert.True(json.GetProperty("restoreRequested").GetBoolean());
        Assert.Equal(restoreAttempted, json.GetProperty("restoreAttempted").GetBoolean());
        Assert.Equal(before, json.GetProperty("isMinimizedBefore").GetBoolean());
        Assert.Equal(after, json.GetProperty("isMinimizedAfter").GetBoolean());
    }

    [Fact]
    public void UnknownBeforeState_NeverActivatesOrInventsNonMinimizedFacts()
    {
        var calls = 0;
        var result = Verbs.ActivateRestoringWindow(() => null, _ => { calls++; return true; });
        Assert.Equal("window-gone", result.Error);
        Assert.Equal(0, calls);
        Assert.False(result.RestoreAttempted);
        var (exitCode, json) = Write(result);
        Assert.Equal(1, exitCode);
        Assert.False(json.GetProperty("activated").GetBoolean());
        Assert.False(json.TryGetProperty("isMinimizedBefore", out _));
        Assert.False(json.TryGetProperty("isMinimizedAfter", out _));
    }

    [Fact]
    public void WindowDisappearingAfterActivation_IsNotVerifiedSuccess()
    {
        var reads = 0;
        var result = Verbs.ActivateRestoringWindow(() => ++reads == 1 ? true : null, attempted =>
        {
            attempted();
            return true;
        });
        Assert.Equal("window-gone", result.Error);
        Assert.True(result.IsMinimizedBefore);
        Assert.Null(result.IsMinimizedAfter);
        Assert.True(result.RestoreAttempted);
        Assert.False(result.Activated);
        Assert.Equal(1, Write(result).ExitCode);
    }

    [Fact]
    public void RestoreFailureFollowedByObservedClosure_ReportsWindowGone()
    {
        var reads = 0;
        var result = Verbs.ActivateRestoringWindow(() => ++reads == 1 ? true : null, attempted =>
        {
            attempted();
            throw new ForegroundActivationException("restoration was not verified");
        });
        Assert.Equal("window-gone", result.Error);
        Assert.True(result.IsMinimizedBefore);
        Assert.Null(result.IsMinimizedAfter);
        Assert.True(result.RestoreAttempted);
        Assert.False(result.Activated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreExceptions_RetainAttemptAndAvailableObservations(bool gone)
    {
        var reads = 0;
        var result = Verbs.ActivateRestoringWindow(() => ++reads == 1 ? true : gone ? null : true, attempted =>
        {
            attempted();
            if (gone) throw new WindowGoneException(new IntPtr(0x123));
            throw new ForegroundActivationException("restore did not finish within 1000 ms");
        });
        Assert.Equal(gone ? "window-gone" : "stale-context", result.Error);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.IsMinimizedBefore);
        Assert.Equal(gone ? null : (bool?)true, result.IsMinimizedAfter);
        Assert.False(result.Activated);
        Assert.Equal(1, Write(result).ExitCode);
    }

    [Theory]
    [InlineData("--restore=yes")]
    [InlineData("--restore=")]
    [InlineData("--restore=1")]
    [InlineData("--restore")]
    public void Cli_RejectsInvalidRestoreBeforeWindowAccess(string argument)
    {
        var result = UiCli.Run("activate", "--hwnd", "0", argument);
        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("restore", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("not-a-hwnd")]
    public void Cli_RejectsInvalidHandlesWithRestore(string hwnd)
    {
        var result = UiCli.Run("activate", $"--hwnd={hwnd}", "--restore", "true");
        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void Cli_RestoreStillRequiresExplicitHwnd()
    {
        var result = UiCli.Run("activate", "--restore", "true");
        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Equal("--hwnd is required.", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mcp_ForwardsRestoreAndKeepsPositionalCancellationTokenCompatibility(bool? restore)
    {
        var window = FixtureWindow.Create(() => new Button());
        var hwnd = window.HwndArgument;
        window.Dispose();
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        var result = restore is null
            ? await tools.Activate(hwnd, CancellationToken.None)
            : await tools.Activate(hwnd, CancellationToken.None, restore);
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(1, result.ExitCode);
        var json = result.Result!.Value;
        Assert.Equal("window-gone", json.GetProperty("error").GetString());
        Assert.Equal(restore == true, json.TryGetProperty("restoreRequested", out _));
        if (restore == true)
        {
            Assert.True(json.GetProperty("restoreRequested").GetBoolean());
            Assert.False(json.GetProperty("restoreAttempted").GetBoolean());
            Assert.False(json.TryGetProperty("isMinimizedBefore", out _));
        }
    }

    [Fact]
    public void Mcp_RetainsMutatingAnnotationsAndAppendsOptionalRestoreAfterToken()
    {
        var method = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.Activate))!;
        var annotation = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("ui_activate", annotation.Name);
        Assert.False(annotation.ReadOnly);
        Assert.True(annotation.Destructive);
        Assert.True(annotation.Idempotent);
        Assert.False(annotation.OpenWorld);
        var parameters = method.GetParameters();
        Assert.Equal(new[] { "hwnd", "cancellationToken", "restore" }, parameters.Select(parameter => parameter.Name));
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.Equal(typeof(bool?), parameters[2].ParameterType);
        Assert.True(parameters[2].IsOptional);
        Assert.Null(parameters[2].DefaultValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [Trait("Category", "InteractiveDesktop")]
    public void Cli_OmittedOrFalseDoesNotRestoreAndKeepsLegacyEnvelope(string? restore)
    {
        Button button = null!;
        using var window = FixtureWindow.Create(() => button = new Button());
        window.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Minimized);
        Assert.True(NativeMethods.IsIconic(window.Hwnd));
        var args = new List<string> { "activate", "--hwnd", window.HwndArgument };
        if (restore is not null) args.AddRange(new[] { "--restore", restore });
        var result = UiCli.Run(args.ToArray());
        output.WriteLine(result.RawOutput);
        Assert.True(NativeMethods.IsIconic(window.Hwnd));
        Assert.False(result.Json.TryGetProperty("restoreRequested", out _));
        Assert.False(result.Json.TryGetProperty("restoreAttempted", out _));
        if (result.Success)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Json.GetProperty("activated").GetBoolean());
            Assert.Equal(2, result.Json.EnumerateObject().Count());
        }
        else
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("stale-context", result.ErrorCode);
        }
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void Cli_RestoreTrueRestoresActualMinimizedWindowAndVerifiesForeground()
    {
        Button button = null!;
        using var window = FixtureWindow.Create(() => button = new Button());
        window.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Minimized);
        Assert.True(NativeMethods.IsIconic(window.Hwnd));
        var result = UiCli.Run("activate", "--hwnd", window.HwndArgument, "--restore", "true");
        output.WriteLine(result.RawOutput);
        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("restoreAttempted").GetBoolean());
        Assert.True(result.Json.GetProperty("isMinimizedBefore").GetBoolean());
        Assert.False(result.Json.GetProperty("isMinimizedAfter").GetBoolean());
        Assert.False(NativeMethods.IsIconic(window.Hwnd));
        Assert.Equal(window.Hwnd, NativeMethods.GetForegroundWindow());
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    [Trait("Category", "InteractiveDesktop")]
    public void Cli_RestoreTruePreservesNonMinimizedPlacement(WindowState state)
    {
        Button button = null!;
        using var window = FixtureWindow.Create(() => button = new Button());
        window.Invoke(() => Window.GetWindow(button).WindowState = state);
        var before = Bounds(window.Hwnd);
        Assert.False(NativeMethods.IsIconic(window.Hwnd));
        var result = UiCli.Run("activate", "--hwnd", window.HwndArgument, "--restore", "true");
        output.WriteLine(result.RawOutput);
        Assert.True(result.Success, result.RawOutput);
        Assert.False(result.Json.GetProperty("restoreAttempted").GetBoolean());
        Assert.False(result.Json.GetProperty("isMinimizedBefore").GetBoolean());
        Assert.False(result.Json.GetProperty("isMinimizedAfter").GetBoolean());
        Assert.Equal(state, window.Invoke(() => Window.GetWindow(button).WindowState));
        Assert.Equal(before, Bounds(window.Hwnd));
        Assert.Equal(window.Hwnd, NativeMethods.GetForegroundWindow());
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void Cli_RestoreTrueDoesNotRestoreAnUnresponsiveMinimizedWindow()
    {
        Button button = null!;
        using var window = FixtureWindow.Create(() => button = new Button());
        window.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Minimized);
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        window.Invoke(() => Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            blocked.Set();
            while (!release.IsSet) Thread.Sleep(20);
        })));
        try
        {
            Assert.True(blocked.Wait(TimeSpan.FromSeconds(5)));
            var result = UiCli.Run("activate", "--hwnd", window.HwndArgument, "--restore", "true");
            Assert.Equal("window-not-responding", result.ErrorCode);
            Assert.False(result.Json.GetProperty("restoreAttempted").GetBoolean());
            Assert.True(NativeMethods.IsIconic(window.Hwnd));
        }
        finally
        {
            release.Set();
            window.Invoke(() => { });
        }
    }

    private static (int Left, int Top, int Right, int Bottom) Bounds(IntPtr hwnd)
    {
        Assert.True(NativeMethods.GetWindowRect(hwnd, out var rect));
        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static (int ExitCode, JsonElement Json) Write(Verbs.ActivationRestoreResult result)
    {
        using var output = new StringWriter();
        var original = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(output);
            exitCode = Verbs.WriteActivationRestoreResult(new IntPtr(0x123), result);
        }
        finally { Console.SetOut(original); }
        using var json = JsonDocument.Parse(output.ToString());
        return (exitCode, json.RootElement.Clone());
    }
}
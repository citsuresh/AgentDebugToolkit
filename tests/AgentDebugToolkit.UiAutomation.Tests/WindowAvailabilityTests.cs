using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class WindowAvailabilityTests
{
    public static IEnumerable<object[]> HwndCommands()
    {
        foreach (var verb in new[] { "inspect", "screenshot", "activate", "read-visible-text" })
        {
            yield return new object[] { verb, Array.Empty<string>() };
        }

        var selector = new[] { "--strategy", "Name", "--value", "Target" };
        foreach (var verb in new[] { "click", "right-click", "double-click", "move-mouse", "get-text",
            "find-first", "find-all", "scroll-into-view" })
        {
            yield return new object[] { verb, selector };
        }

        yield return new object[] { "type", selector.Concat(new[] { "--text", "test" }).ToArray() };
        yield return new object[] { "send-keys", selector.Concat(new[] { "--keys", "{ESC}" }).ToArray() };
        yield return new object[] { "drag", selector.Concat(new[] { "--targetX", "20", "--targetY", "20" }).ToArray() };
        yield return new object[] { "wait-for-element", selector.Concat(new[] { "--timeoutMs", "0" }).ToArray() };
        yield return new object[] { "scroll-container", selector.Concat(new[] { "--verticalPercent", "50" }).ToArray() };
        yield return new object[] { "submit-chat-message", new[] { "--inputAutomationId", "Target", "--text", "test" } };
        yield return new object[] { "set-grid-cell", new[] { "--gridStrategy", "Name", "--gridValue", "Grid",
            "--rowStrategy", "Name", "--rowValue", "Row", "--columnIndex", "0", "--text", "test" } };
    }

    [Theory]
    [MemberData(nameof(HwndCommands))]
    public void HwndVerbs_ClosedFixtureReturnsWindowGone(string verb, string[] options)
    {
        var window = FixtureWindow.Create(() => new Button { Content = "Target" });
        var hwnd = window.Hwnd;
        var hwndArgument = window.HwndArgument;
        window.Dispose();
        Assert.False(IsWindow(hwnd));

        var result = UiCli.Run(new[] { verb, "--hwnd", hwndArgument }.Concat(options).ToArray());

        Assert.False(result.Success, result.RawOutput);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("window-gone", result.ErrorCode);
        var message = result.Json.GetProperty("message").GetString();
        Assert.Contains(hwndArgument, message);
        Assert.Contains("list-windows --processName <name>", message);
        Assert.Contains("new hwnd and pid", message);
        if (verb == "submit-chat-message")
        {
            Assert.Equal("resolve-window", result.Json.GetProperty("step").GetString());
        }
    }

    [Theory]
    [InlineData("click")]
    [InlineData("get-text")]
    [InlineData("scroll-container")]
    public void HwndVerbs_ClosedScopeReturnsWindowGone(string verb)
    {
        using var target = FixtureWindow.Create(() => new Button { Content = "Target" });
        var scope = FixtureWindow.Create(() => new Button { Content = "Scope" });
        var scopeHwnd = scope.HwndArgument;
        scope.Dispose();

        var options = new[] { verb, "--hwnd", target.HwndArgument,
            "--scopeHwnd", scopeHwnd, "--strategy", "Name", "--value", "Target" };
        if (verb == "scroll-container")
        {
            options = options.Concat(new[] { "--verticalPercent", "50" }).ToArray();
        }

        var result = UiCli.Run(options);

        Assert.Equal("window-gone", result.ErrorCode);
        Assert.Contains(scopeHwnd, result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("screenshot")]
    [InlineData("activate")]
    public void HwndVerbs_ExistingHungFixtureReturnsWindowNotResponding(string verb)
    {
        using var window = FixtureWindow.Create(() => new Button { Content = "Target" });
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        window.Invoke(() =>
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                blocked.Set();
                // An STA handle wait can pump sent messages, making WM_NULL appear responsive.
                while (!release.IsSet)
                {
                    Thread.Sleep(20);
                }
            }));
        });

        string? screenshotPath = null;
        try
        {
            Assert.True(blocked.Wait(TimeSpan.FromSeconds(5)), "The fixture dispatcher did not block.");
            Assert.True(IsWindow(window.Hwnd));

            var result = UiCli.Run(verb, "--hwnd", window.HwndArgument);
            if (result.Json.TryGetProperty("screenshotPath", out var path))
            {
                screenshotPath = path.GetString();
            }

            Assert.False(result.Success, result.RawOutput);
            Assert.Equal("window-not-responding", result.ErrorCode);
            Assert.True(IsWindow(window.Hwnd));
        }
        finally
        {
            release.Set();
            window.Invoke(() => { });
            if (screenshotPath is not null && File.Exists(screenshotPath))
            {
                File.Delete(screenshotPath);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
}
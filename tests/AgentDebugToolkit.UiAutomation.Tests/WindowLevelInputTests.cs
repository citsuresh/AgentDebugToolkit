using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

/// <summary>
/// Window-level input: send-keys without a selector and paste-text. Both target whatever has
/// focus in the --hwnd window, which is how an app whose content UI Automation cannot see is
/// driven. Tests that need real foreground activation carry the InteractiveDesktop trait.
/// </summary>
public sealed class WindowLevelInputTests
{
    [Fact]
    public void PasteText_RequiresHwnd()
    {
        var result = UiCli.Run("paste-text", "--text", "hello");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--hwnd", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("--text=")]
    [InlineData("--nothing")]
    public void PasteText_RequiresNonEmptyText(string option)
    {
        var args = option == "--nothing"
            ? new[] { "paste-text", "--hwnd", "0x1" }
            : new[] { "paste-text", "--hwnd", "0x1", option };

        var result = UiCli.Run(args);

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--text", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void SendKeysWithoutSelector_RequiresHwndBeforeAnyWindowResolution()
    {
        // With --pid and no --hwnd, resolution could fail first with ambiguous-window or
        // stale-context; the missing-hwnd argument error must win.
        var result = UiCli.Run("send-keys", "--pid", "1", "--keys", "a");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--hwnd", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("5001")]
    [InlineData("abc")]
    [InlineData("")]
    public void PasteText_RejectsOutOfRangeRestoreDelay(string value)
    {
        var result = UiCli.Run("paste-text", "--hwnd", "0x1", "--text", "hello", $"--restoreDelayMs={value}");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--restoreDelayMs", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void PasteText_RejectsInvalidHwnd()
    {
        var result = UiCli.Run("paste-text", "--hwnd", "not-a-window", "--text", "hello");

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("Invalid hwnd", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void PasteText_ClosedWindowReturnsWindowGone()
    {
        var window = FixtureWindow.Create(() => new TextBox());
        var staleHwnd = window.HwndArgument;
        window.Dispose();

        var result = UiCli.Run("paste-text", "--hwnd", staleHwnd, "--text", "hello");

        Assert.Equal("window-gone", result.ErrorCode);
    }

    [Theory]
    [InlineData("--focusMode", "click")]
    [InlineData("--focusMode", "set-focus")]
    public void SendKeysWithoutSelector_RejectsFocusModeOtherThanNone(string flag, string value)
    {
        using var window = FixtureWindow.Create(() => new TextBox());

        var result = UiCli.Run("send-keys", "--hwnd", window.HwndArgument, "--keys", "a", flag, value);

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("focusMode", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void SendKeysWithoutSelector_RejectsScopeHwnd()
    {
        using var window = FixtureWindow.Create(() => new TextBox());

        var result = UiCli.Run("send-keys", "--hwnd", window.HwndArgument, "--keys", "a",
            "--scopeHwnd", window.HwndArgument);

        Assert.Equal("invalid-argument", result.ErrorCode);
        Assert.Contains("--scopeHwnd", result.Json.GetProperty("message").GetString());
    }

    [Fact]
    public void SendKeysWithOnlyStrategy_StillUsesTheSelectorForm()
    {
        using var window = FixtureWindow.Create(() => new TextBox());

        var result = UiCli.Run("send-keys", "--hwnd", window.HwndArgument, "--keys", "a",
            "--strategy", "Name");

        // The selector form is unchanged: a half-specified selector is an argument error, not a
        // silent fall-through to the window-level form.
        Assert.False(result.Success, result.RawOutput);
        Assert.Equal("invalid-argument", result.ErrorCode);
    }

    [Fact]
    public void SendKeysWithoutSelector_InvalidSyntaxIsInvalidArgument()
    {
        using var window = FixtureWindow.Create(() => new TextBox());

        var result = UiCli.Run("send-keys", "--hwnd", window.HwndArgument, "--keys", "{FOO}");

        // Either the syntax error or, on a machine that refuses the foreground switch, the
        // activation error: never a success and never an unhandled exception.
        Assert.False(result.Success, result.RawOutput);
        Assert.Contains(result.ErrorCode, new[] { "invalid-argument", "foreground-activation-failed" });
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void SendKeysWithoutSelector_TypesIntoTheFocusedControl()
    {
        TextBox box = null!;
        using var window = FixtureWindow.Create(() => box = new TextBox { AcceptsReturn = true });
        window.Invoke(() => Keyboard.Focus(box));

        var result = UiCli.Run("send-keys", "--hwnd", window.HwndArgument, "--keys", "abc");

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal("window", result.Json.GetProperty("focusMethod").GetString());
        Assert.True(Poll.Until(() => window.Invoke(() => box.Text) == "abc", TimeSpan.FromSeconds(5)),
            "The focused TextBox did not receive the keys.");
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void PasteText_PastesMultilineTextWithSendKeysSpecialCharacters()
    {
        TextBox box = null!;
        using var window = FixtureWindow.Create(() => box = new TextBox { AcceptsReturn = true });
        window.Invoke(() => Keyboard.Focus(box));
        const string text = "line one + ^ % ~ {x}\r\nline two";

        var result = UiCli.Run("paste-text", "--hwnd", window.HwndArgument, "--text", text);

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal("clipboard-paste-focus", result.Json.GetProperty("method").GetString());
        Assert.Equal(text.Length, result.Json.GetProperty("length").GetInt32());
        Assert.True(result.Json.TryGetProperty("clipboardSnapshotComplete", out var snapshotComplete));
        Assert.True(snapshotComplete.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False);
        Assert.True(Poll.Until(
            () => window.Invoke(() => box.Text).Replace("\r\n", "\n") == text.Replace("\r\n", "\n"),
            TimeSpan.FromSeconds(5)), "The focused TextBox did not receive the pasted text.");
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentDebugToolkit.Mcp;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class WindowScreenshotTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Screenshot_ReportsPhysicalBoundsImageSizeAndDpiScale(bool maximized, bool useMcp)
    {
        Button button = null!;
        using var window = FixtureWindow.Create(() => button = new Button { Content = "Screenshot target" });
        if (maximized)
        {
            window.Invoke(() => Window.GetWindow(button).WindowState = WindowState.Maximized);
            window.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(WindowState.Maximized, window.Invoke(() => Window.GetWindow(button).WindowState));
        }

        var (expectedRect, expectedDpi) = ReadPhysicalMetadata(window.Hwnd);
        var result = await Capture(window.HwndArgument, useMcp);
        string? path = null;
        try
        {
            if (result.TryGetProperty("screenshotPath", out var pathElement))
            {
                path = pathElement.GetString();
            }

            Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
            Assert.False(string.IsNullOrEmpty(path));
            Assert.True(File.Exists(path), $"Screenshot file missing: {path}");
            var rect = result.GetProperty("windowRect");
            Assert.Equal(expectedRect.Left, rect.GetProperty("left").GetInt32());
            Assert.Equal(expectedRect.Top, rect.GetProperty("top").GetInt32());
            Assert.Equal(expectedRect.Right, rect.GetProperty("right").GetInt32());
            Assert.Equal(expectedRect.Bottom, rect.GetProperty("bottom").GetInt32());
            Assert.Equal(expectedDpi / 96.0, result.GetProperty("dpiScale").GetDouble());

            using var stream = File.OpenRead(path!);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(expectedRect.Right - expectedRect.Left, frame.PixelWidth);
            Assert.Equal(expectedRect.Bottom - expectedRect.Top, frame.PixelHeight);
            Assert.Equal(frame.PixelWidth, result.GetProperty("imageWidth").GetInt32());
            Assert.Equal(frame.PixelHeight, result.GetProperty("imageHeight").GetInt32());
        }
        finally
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task McpScreenshot_ClosedFixtureReturnsWindowGoneWithoutImage()
    {
        var window = FixtureWindow.Create(() => new Button { Content = "Screenshot target" });
        var hwnd = window.HwndArgument;
        window.Dispose();

        var result = await Capture(hwnd, useMcp: true);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("window-gone", result.GetProperty("error").GetString());
        Assert.Contains("list-windows --processName <name>", result.GetProperty("message").GetString());
        Assert.False(result.TryGetProperty("screenshotPath", out _));
        Assert.False(result.TryGetProperty("windowRect", out _));
    }

    private static async Task<JsonElement> Capture(string hwnd, bool useMcp)
    {
        if (!useMcp)
        {
            return UiCli.Run("screenshot", "--hwnd", hwnd).Json;
        }

        var tools = new UiAutomationTools(new CliRunner(
            NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        var invocation = await tools.Screenshot(hwnd);
        Assert.Null(invocation.WrapperErrorCode);
        Assert.NotNull(invocation.Result);
        return invocation.Result.Value;
    }

    private static (WindowRect Rect, uint Dpi) ReadPhysicalMetadata(IntPtr hwnd)
    {
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previousContext);
        try
        {
            Assert.True(GetWindowRect(hwnd, out var rect));
            var dpi = GetDpiForWindow(hwnd);
            Assert.NotEqual(0u, dpi);
            return (rect, dpi);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previousContext);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
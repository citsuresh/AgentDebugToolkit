using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentDebugToolkit.Mcp;
using AgentDebugToolkit.UiAutomation.Cli;
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
            Assert.Equal(new[] { "dpiScale", "imageHeight", "imageWidth", "screenshotPath", "success", "windowRect" },
                result.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
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

    [Theory]
    [InlineData(false, "21,33,101,81", null)]
    [InlineData(true, "21,33,101,81", null)]
    [InlineData(false, "21,33,101,81", 0.5)]
    [InlineData(true, "21,33,101,81", 0.5)]
    [InlineData(false, "21,33,101,81", 0.1)]
    [InlineData(true, "21,33,101,81", 0.1)]
    [InlineData(false, null, 0.5)]
    [InlineData(true, null, 0.5)]
    [InlineData(false, null, 1.0)]
    [InlineData(true, null, 1.0)]
    [InlineData(false, "0,0,1,1", 0.1)]
    [InlineData(true, "0,0,1,1", 0.1)]
    public async Task Screenshot_RegionAndScaleDescribeSavedPng(bool useMcp, string? region, double? scale)
    {
        using var window = FixtureWindow.Create(() => new Button { Content = "Screenshot target" });
        var (rect, dpi) = ReadPhysicalMetadata(window.Hwnd);
        var expectedRegion = region is null
            ? new[] { 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top }
            : region.Split(',').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        var result = await Capture(window.HwndArgument, useMcp, region, scale);

        AssertTransformedImage(result, rect, dpi, expectedRegion, scale ?? 1.0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Screenshot_RegionMayTouchRightAndBottomEdges(bool useMcp)
    {
        using var window = FixtureWindow.Create(() => new Button { Content = "Screenshot target" });
        var (rect, dpi) = ReadPhysicalMetadata(window.Hwnd);
        var expectedRegion = new[] { rect.Right - rect.Left - 21, rect.Bottom - rect.Top - 17, 21, 17 };
        var region = string.Join(",", expectedRegion.Select(value => value.ToString(CultureInfo.InvariantCulture)));

        var result = await Capture(window.HwndArgument, useMcp, region);

        AssertTransformedImage(result, rect, dpi, expectedRegion, 1.0);
    }

    public static IEnumerable<object[]> InvalidRegions()
    {
        foreach (var region in new[] { "", "1,2,3", "1,2,3,4,5", "a,2,3,4", "-11,0,3,4", "0,-1,3,4",
            "0,0,0,1", "0,0,1,-1", "2147483647,0,1,1", "0,2147483647,1,1",
            "0,0,2147483647,1", "2147483648,0,1,1" })
        {
            yield return new object[] { false, region };
            yield return new object[] { true, region };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRegions))]
    public async Task Screenshot_InvalidRegionReturnsInvalidArgumentWithoutImage(bool useMcp, string region)
    {
        using var window = FixtureWindow.Create(() => new Button { Content = "Screenshot target" });

        var result = await Capture(window.HwndArgument, useMcp, region);

        AssertInvalidOption(result, "--region");
    }

    [Theory]
    [InlineData(false, 0.09)]
    [InlineData(true, 0.09)]
    [InlineData(false, 1.01)]
    [InlineData(true, 1.01)]
    [InlineData(false, double.NaN)]
    [InlineData(true, double.NaN)]
    [InlineData(false, double.PositiveInfinity)]
    [InlineData(true, double.PositiveInfinity)]
    [InlineData(false, double.NegativeInfinity)]
    [InlineData(true, double.NegativeInfinity)]
    public async Task Screenshot_InvalidScaleReturnsInvalidArgumentWithoutImage(bool useMcp, double scale)
    {
        using var window = FixtureWindow.Create(() => new Button { Content = "Screenshot target" });

        var result = await Capture(window.HwndArgument, useMcp, scale: scale);

        AssertInvalidOption(result, "--scale");
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0,5")]
    [InlineData("")]
    public void Screenshot_MalformedScaleReturnsInvalidArgument(string scale)
    {
        var result = UiCli.Run("screenshot", "--hwnd", "not-a-window", $"--scale={scale}");

        AssertInvalidOption(result.Json, "--scale");
    }

    [Fact]
    public async Task McpScreenshot_ForwardsScaleInvariantly()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using var window = FixtureWindow.Create(() => new Button { Content = "Screenshot target" });
            var (rect, dpi) = ReadPhysicalMetadata(window.Hwnd);

            var result = await Capture(window.HwndArgument, true, "21,33,101,81", 0.5);

            AssertTransformedImage(result, rect, dpi, new[] { 21, 33, 101, 81 }, 0.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void CropAndScale_UnscaledCropPreservesEverySourcePixel()
    {
        using var bitmap = new System.Drawing.Bitmap(12, 8);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(x * 20, y * 30, 100));
            }
        }

        var region = new System.Drawing.Rectangle(3, 2, 5, 4);
        using var cropped = ScreenshotHelper.CropAndScale(bitmap, region, 1.0);

        Assert.Equal(region.Width, cropped.Width);
        Assert.Equal(region.Height, cropped.Height);
        for (var y = 0; y < cropped.Height; y++)
        {
            for (var x = 0; x < cropped.Width; x++)
            {
                Assert.Equal(bitmap.GetPixel(region.X + x, region.Y + y), cropped.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void CropAndScale_DownsizesOnlyTheRegionWithCeilingRounding()
    {
        using var bitmap = new System.Drawing.Bitmap(20, 10);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Blue);
            graphics.FillRectangle(System.Drawing.Brushes.Red, 4, 2, 9, 5);
        }

        using var scaled = ScreenshotHelper.CropAndScale(bitmap, new System.Drawing.Rectangle(4, 2, 9, 5), 0.5);

        Assert.Equal(5, scaled.Width);
        Assert.Equal(3, scaled.Height);
        var pixel = scaled.GetPixel(1, 1);
        Assert.Equal(255, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
    }

    private static void AssertInvalidOption(JsonElement result, string option)
    {
        Assert.False(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal("invalid-argument", result.GetProperty("error").GetString());
        Assert.Contains(option, result.GetProperty("message").GetString());
        Assert.False(result.TryGetProperty("screenshotPath", out _));
        Assert.False(result.TryGetProperty("region", out _));
    }

    private static void AssertTransformedImage(JsonElement result, WindowRect rect, uint dpi, int[] expectedRegion, double scale)
    {
        var path = result.TryGetProperty("screenshotPath", out var pathElement) ? pathElement.GetString() : null;
        try
        {
            Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
            var reportedRect = result.GetProperty("windowRect");
            Assert.Equal(rect.Left, reportedRect.GetProperty("left").GetInt32());
            Assert.Equal(rect.Top, reportedRect.GetProperty("top").GetInt32());
            Assert.Equal(rect.Right, reportedRect.GetProperty("right").GetInt32());
            Assert.Equal(rect.Bottom, reportedRect.GetProperty("bottom").GetInt32());
            Assert.Equal(dpi / 96.0, result.GetProperty("dpiScale").GetDouble());
            var region = result.GetProperty("region");
            Assert.Equal(expectedRegion, new[] { region.GetProperty("x").GetInt32(), region.GetProperty("y").GetInt32(),
                region.GetProperty("width").GetInt32(), region.GetProperty("height").GetInt32() });
            Assert.Equal(scale, result.GetProperty("scale").GetDouble());

            using var stream = File.OpenRead(path!);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal((int)Math.Ceiling(expectedRegion[2] * scale), frame.PixelWidth);
            Assert.Equal((int)Math.Ceiling(expectedRegion[3] * scale), frame.PixelHeight);
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

    private static async Task<JsonElement> Capture(string hwnd, bool useMcp, string? region = null, double? scale = null)
    {
        if (!useMcp)
        {
            var args = new List<string> { "screenshot", "--hwnd", hwnd };
            if (region is not null)
            {
                args.AddRange(new[] { "--region", region });
            }
            if (scale is not null)
            {
                args.AddRange(new[] { "--scale", scale.Value.ToString("R", CultureInfo.InvariantCulture) });
            }

            return UiCli.Run(args.ToArray()).Json;
        }

        var tools = new UiAutomationTools(new CliRunner(
            NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        var invocation = await tools.Screenshot(hwnd, CancellationToken.None, region, scale);
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
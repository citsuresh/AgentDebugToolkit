using System.IO;
using System.Windows.Controls;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class InspectScreenshotTests
{
    private static readonly string ScreenshotFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDebugToolkit", "screenshots");

    [Fact]
    public void Inspect_WithoutScreenshotFlag_ReturnsNoScreenshotPathAndSavesNoFile()
    {
        using var window = CreateFixture();
        var before = ListScreenshots();

        var plain = UiCli.Run("inspect", "--hwnd", window.HwndArgument);
        var filtered = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--nameContains", "Inspect target");
        var explicitOff = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--screenshot", "false");

        Assert.True(plain.Success, plain.RawOutput);
        Assert.False(plain.Json.TryGetProperty("screenshotPath", out _), plain.RawOutput);
        Assert.True(filtered.Success, filtered.RawOutput);
        Assert.False(filtered.Json.TryGetProperty("screenshotPath", out _), filtered.RawOutput);
        Assert.True(filtered.Json.TryGetProperty("matchCount", out _), filtered.RawOutput);
        Assert.True(explicitOff.Success, explicitOff.RawOutput);
        Assert.False(explicitOff.Json.TryGetProperty("screenshotPath", out _), explicitOff.RawOutput);
        Assert.Empty(ListScreenshots().Except(before, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inspect_WithScreenshotTrue_ReturnsPathToSavedFile()
    {
        using var window = CreateFixture();

        var result = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--screenshot", "true");

        string? path = null;
        try
        {
            Assert.True(result.Success, result.RawOutput);
            Assert.True(result.Json.TryGetProperty("screenshotPath", out var pathElement), result.RawOutput);
            path = pathElement.GetString();
            Assert.False(string.IsNullOrEmpty(path), result.RawOutput);
            Assert.True(File.Exists(path), $"Screenshot file missing: {path}");
        }
        finally
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static HashSet<string> ListScreenshots()
        => Directory.Exists(ScreenshotFolder)
            ? new HashSet<string>(Directory.GetFiles(ScreenshotFolder), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static FixtureWindow CreateFixture()
        => FixtureWindow.Create(() => new Button { Content = "Inspect target" });
}

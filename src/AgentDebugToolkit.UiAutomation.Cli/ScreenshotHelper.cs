using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Automation;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal static class ScreenshotHelper
{
    public static string Capture(AutomationElement element)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDebugToolkit", "screenshots");
        Directory.CreateDirectory(dir);

        var r = element.Current.BoundingRectangle;
        var path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        var width = Math.Max(1, (int)r.Width);
        var height = Math.Max(1, (int)r.Height);

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen((int)r.X, (int)r.Y, 0, 0, new Size(width, height));
        }
        bitmap.Save(path, ImageFormat.Png);

        return path;
    }
}

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Automation;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal static class ScreenshotHelper
{
    public static string Capture(AutomationElement element)
    {
        var r = element.Current.BoundingRectangle;
        return CaptureRegion((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    /// <summary>
    /// Read-only fallback for windows whose content (e.g. a chat/webview panel) isn't exposed
    /// meaningfully through UIA. Captures the window itself via PrintWindow with
    /// PW_RENDERFULLCONTENT (needed for modern DirectComposition/WPF-rendered windows, e.g.
    /// Visual Studio's own UI, which draw outside the classic GDI path that plain PrintWindow
    /// handles) — this captures the target hwnd's own content regardless of z-order/obscurity,
    /// unlike a desktop screen-coordinate copy. Falls back to a CopyFromScreen capture of the
    /// window's screen rect (via GetWindowRect) if PrintWindow reports failure; that fallback
    /// only reflects the target window's true content when it is topmost/unobscured at its
    /// screen position — see docs/CLI_CONTRACT.md.
    /// </summary>
    public static string CaptureWindow(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException($"GetWindowRect failed for hwnd '0x{hwnd.ToInt64():X}'.");
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        var printed = TryPrintWindow(hwnd, width, height);
        if (printed is not null)
        {
            return SaveBitmap(printed);
        }

        return CaptureRegion(rect.Left, rect.Top, width, height);
    }

    /// <summary>
    /// Attempts PrintWindow with PW_RENDERFULLCONTENT. Returns null (caller falls back to
    /// CopyFromScreen) if PrintWindow reports failure or the target hwnd is invalid.
    /// </summary>
    private static Bitmap? TryPrintWindow(IntPtr hwnd, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            var hdc = g.GetHdc();
            bool ok;
            try
            {
                ok = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }

            if (!ok)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }

    private static string SaveBitmap(Bitmap bitmap)
    {
        using (bitmap)
        {
            var dir = ScreenshotDirectory();
            var path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
    }

    private static string ScreenshotDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDebugToolkit", "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CaptureRegion(int x, int y, int width, int height)
    {
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);

        var bitmap = new Bitmap(safeWidth, safeHeight);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(safeWidth, safeHeight));
        }

        return SaveBitmap(bitmap);
    }
}

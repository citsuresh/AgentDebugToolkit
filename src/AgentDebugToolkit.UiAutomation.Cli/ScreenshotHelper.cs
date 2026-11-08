using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Automation;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal sealed record WindowScreenshotResult(
    string ScreenshotPath, NativeMethods.RECT WindowRect, int ImageWidth, int ImageHeight, double DpiScale,
    Rectangle? Region = null, double? Scale = null);

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
    public static WindowScreenshotResult CaptureWindow(IntPtr hwnd, Rectangle? region = null, double? scale = null)
    {
        NativeMethods.EnsureWindowExists(hwnd);
        if (scale is { } requestedScale && (!double.IsFinite(requestedScale) || requestedScale < 0.1 || requestedScale > 1.0))
        {
            throw new ArgumentException("--scale must be a finite number from 0.1 to 1.0.", nameof(scale));
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            NativeMethods.EnsureWindowExists(hwnd);
            throw new InvalidOperationException($"GetWindowRect failed for hwnd '0x{hwnd.ToInt64():X}'.");
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The target window rectangle has no positive area; no screenshot was saved.");
        }

        var sourceRegion = region ?? new Rectangle(0, 0, width, height);
        if (sourceRegion.X < 0 || sourceRegion.Y < 0 || sourceRegion.Width <= 0 || sourceRegion.Height <= 0
            || sourceRegion.Width > width || sourceRegion.Height > height
            || sourceRegion.X > width - sourceRegion.Width || sourceRegion.Y > height - sourceRegion.Height)
        {
            throw new ArgumentException("--region must have non-negative x/y, positive width/height, and lie entirely inside the window image.", nameof(region));
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            NativeMethods.EnsureWindowExists(hwnd);
            throw new InvalidOperationException("GetDpiForWindow failed; no screenshot was saved.");
        }

        var printed = TryPrintWindow(hwnd, width, height);
        if (!NativeMethods.IsWindow(hwnd))
        {
            printed?.Dispose();
            throw new WindowGoneException(hwnd);
        }

        using var bitmap = printed ?? CaptureScreenRegion(rect.Left, rect.Top, width, height);
        if (region is null && scale is null)
        {
            return new WindowScreenshotResult(SaveBitmap(bitmap), rect, width, height, dpi / 96.0);
        }

        var outputScale = scale ?? 1.0;
        using var transformed = CropAndScale(bitmap, sourceRegion, outputScale);
        return new WindowScreenshotResult(SaveBitmap(transformed), rect, transformed.Width, transformed.Height,
            dpi / 96.0, sourceRegion, outputScale);
    }

    internal static Bitmap CropAndScale(Bitmap bitmap, Rectangle region, double scale)
    {
        if (scale == 1.0)
        {
            return bitmap.Clone(region, PixelFormat.Format32bppArgb);
        }

        using var cropped = bitmap.Clone(region, PixelFormat.Format32bppArgb);
        var scaled = new Bitmap((int)Math.Ceiling(region.Width * scale), (int)Math.Ceiling(region.Height * scale));
        try
        {
            using var graphics = Graphics.FromImage(scaled);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            // Keep the requested scale rather than stretching to the rounded output dimensions.
            graphics.ScaleTransform((float)scale, (float)scale);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(cropped, new Rectangle(0, 0, region.Width, region.Height),
                0, 0, region.Width, region.Height, GraphicsUnit.Pixel, attributes);
            return scaled;
        }
        catch
        {
            scaled.Dispose();
            throw;
        }
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
        var dir = ScreenshotDirectory();
        var path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
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
        using var bitmap = CaptureScreenRegion(x, y, Math.Max(1, width), Math.Max(1, height));
        return SaveBitmap(bitmap);
    }

    private static Bitmap CaptureScreenRegion(int x, int y, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}

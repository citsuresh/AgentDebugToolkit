using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal enum CursorSessionKind
{
    Orchestrator,
    UiAutomation
}

internal interface ICursorOperations
{
    bool Mark(CursorSessionKind kind);
    bool Restore();
}

internal sealed record CursorSessionResult(string? Kind, string? Owner, bool Changed, string? Warning = null);

internal sealed class CursorSession
{
    private readonly ICursorOperations _cursors;
    private readonly string _flagPath;

    internal static string FlagPath(string? tempDirectory, string fallbackDirectory) =>
        Path.Combine(string.IsNullOrWhiteSpace(tempDirectory) ? fallbackDirectory : tempDirectory,
            "agent-cursor-session.flag");

    internal CursorSession(ICursorOperations cursors, string flagPath)
    {
        _cursors = cursors;
        _flagPath = flagPath;
    }

    internal CursorSessionKind? ActiveKind()
    {
        if (!File.Exists(_flagPath))
        {
            return null;
        }

        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(_flagPath) >= TimeSpan.FromHours(12))
        {
            return null;
        }

        var content = File.ReadAllText(_flagPath);
        return content.Contains("kind=UiAutomation", StringComparison.OrdinalIgnoreCase)
            ? CursorSessionKind.UiAutomation
            : CursorSessionKind.Orchestrator;
    }

    internal CursorSessionResult Start(CursorSessionKind kind)
    {
        var active = ActiveKind();
        if (active is not null && active != kind)
        {
            return new CursorSessionResult(kind.ToString(), active.ToString(), false);
        }

        File.WriteAllText(_flagPath,
            $"kind={kind} pid={Environment.ProcessId} started={DateTime.Now.ToString("o", CultureInfo.InvariantCulture)}{Environment.NewLine}",
            Encoding.ASCII);
        var marked = _cursors.Mark(kind);
        return new CursorSessionResult(kind.ToString(), kind.ToString(), true,
            marked ? null : $"Could not set the {kind} cursor indicator; continuing without it.");
    }

    internal CursorSessionResult Stop(CursorSessionKind? kind)
    {
        var active = ActiveKind();
        if (kind is not null && active is not null && active != kind)
        {
            return new CursorSessionResult(kind.ToString(), active.ToString(), false);
        }

        if (File.Exists(_flagPath))
        {
            File.Delete(_flagPath);
        }

        var restored = _cursors.Restore();
        return new CursorSessionResult(kind?.ToString(), null, true,
            restored ? null : "Could not restore the cursor scheme. Run cursor-session stop without --kind, or sign out and back in.");
    }
}

internal sealed class SystemCursorOperations : ICursorOperations
{
    private const uint SpiSetCursors = 0x0057;
    private static readonly uint[] CursorIds = [32512, 32649, 32513];

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int XHotspot;
        public int YHotspot;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetSystemCursor(IntPtr cursor, uint id);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    public bool Mark(CursorSessionKind kind)
    {
        // The script and CLI share system cursors: reload the user's scheme before copying it.
        if (!Restore())
        {
            return false;
        }

        var dot = kind == CursorSessionKind.Orchestrator
            ? Color.FromArgb(30, 110, 230)
            : Color.FromArgb(30, 170, 70);
        var all = true;
        foreach (var id in CursorIds)
        {
            var cursor = CreateMarkedCursor(id, dot);
            if (cursor == IntPtr.Zero)
            {
                all = false;
            }
            else if (!SetSystemCursor(cursor, id))
            {
                DestroyIcon(cursor);
                all = false;
            }
        }
        return all;
    }

    public bool Restore() => SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0);

    private static IntPtr CreateMarkedCursor(uint id, Color dot)
    {
        var source = LoadCursor(IntPtr.Zero, (IntPtr)id);
        if (source == IntPtr.Zero || !GetIconInfo(source, out var original))
        {
            return IntPtr.Zero;
        }

        DeleteBitmaps(original);
        using var icon = Icon.FromHandle(source);
        using var bitmap = icon.ToBitmap();
        var size = Math.Min(bitmap.Width, bitmap.Height);
        var diameter = size * 0.42f;
        var x = size - diameter - 1;
        var y = size - diameter - 1;
        using (var graphics = Graphics.FromImage(bitmap))
        using (var fill = new SolidBrush(dot))
        using (var outline = new Pen(Color.White, Math.Max(1.5f, size / 16f)))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(fill, x, y, diameter, diameter);
            graphics.DrawEllipse(outline, x, y, diameter, diameter);
        }

        var markedIcon = bitmap.GetHicon();
        try
        {
            if (!GetIconInfo(markedIcon, out var marked))
            {
                return IntPtr.Zero;
            }

            try
            {
                marked.IsIcon = false;
                marked.XHotspot = original.XHotspot;
                marked.YHotspot = original.YHotspot;
                return CreateIconIndirect(ref marked);
            }
            finally
            {
                DeleteBitmaps(marked);
            }
        }
        finally
        {
            DestroyIcon(markedIcon);
        }
    }

    private static void DeleteBitmaps(IconInfo info)
    {
        if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
        if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
    }
}
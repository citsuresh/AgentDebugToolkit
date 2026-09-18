using System.Runtime.InteropServices;
using System.Text;

namespace AgentDebugToolkit.UiAutomation.Cli;

/// <summary>
/// P/Invoke helpers for synthetic input and window queries. See docs/VALIDATION_FINDINGS.md
/// for why synthetic input (rather than UIA InvokePattern/ValuePattern) is the primary
/// interaction mechanism for this class of target application.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPosNative(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the underlying Win32 call fails --
    /// matching the check-and-throw convention <see cref="ScreenshotHelper"/> already uses for
    /// GetWindowRect, the nearest equivalent "read a rect/point, bool success indicator" API in
    /// this codebase, rather than silently returning a fabricated (0,0).
    /// </summary>
    public static (int x, int y) GetCursorPos()
    {
        if (!GetCursorPosNative(out var p))
        {
            throw new InvalidOperationException("GetCursorPos failed.");
        }

        return (p.X, p.Y);
    }

    // SendInput-based mouse button events replace mouse_event (deprecated by Microsoft in favor
    // of SendInput; see https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-mouse_event).
    // Cursor *movement* still goes through SetCursorPos (not deprecated, and simpler than
    // computing SendInput's normalized absolute coordinates against the virtual screen for
    // multi-monitor setups) -- only the button-down/button-up signal itself is migrated.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;

    private static void SendMouseInput(uint dwFlags)
    {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi = new MOUSEINPUT
        {
            dx = 0,
            dy = 0,
            mouseData = 0,
            dwFlags = dwFlags,
            time = 0,
            dwExtraInfo = IntPtr.Zero,
        };

        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    public const uint PW_CLIENTONLY = 0x00000001;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint GW_OWNER = 4;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint WM_NULL = 0x0000;

    public static List<IntPtr> EnumerateVisibleTopLevelWindows(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid == (uint)pid && IsWindowVisible(hwnd))
            {
                windows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        var text = new StringBuilder(length + 1);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static string GetWindowClassName(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        return className.ToString();
    }

    // --- DPI awareness -----------------------------------------------------------------
    // Declaring per-monitor-v2 DPI awareness (falling back to the legacy per-process API on
    // older Windows builds) must happen once, at process startup, before any window/coordinate
    // work occurs. Without this, Windows silently virtualizes every coordinate this process
    // reads (UIA BoundingRectangle, GetWindowRect) and writes (SetCursorPos/SendInput targets)
    // against a scaled "low-res" virtual desktop on any display that isn't at 100% scale --
    // the root cause of click/type/drag landing on the wrong element on such systems.
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDPIAware();

    /// <summary>
    /// Attempts to declare per-monitor-v2 DPI awareness (Windows 10 1703+). Returns false
    /// (rather than throwing) if the API isn't available on this OS version or the call fails,
    /// so the caller can fall back to <see cref="SetProcessDPIAware"/>.
    /// </summary>
    public static bool TrySetPerMonitorDpiAwareness()
    {
        try
        {
            return SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (EntryPointNotFoundException)
        {
            // SetProcessDpiAwarenessContext doesn't exist on pre-1703 Windows 10 / older OSes.
            return false;
        }
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        SendMouseInput(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(50);
        SendMouseInput(MOUSEEVENTF_LEFTUP);
    }

    public static void RightClick(int x, int y)
    {
        SetCursorPos(x, y);
        SendMouseInput(MOUSEEVENTF_RIGHTDOWN);
        Thread.Sleep(50);
        SendMouseInput(MOUSEEVENTF_RIGHTUP);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>
    /// Performs two rapid synthetic left-clicks timed against the OS-configured double-click
    /// threshold (<see cref="GetDoubleClickTime"/>). Uses a shorter per-click button-hold time
    /// (10ms) than a standalone <see cref="Click"/> (50ms), and budgets the inter-click delay so
    /// the full down-to-down round trip -- both clicks' hold time plus the gap between them --
    /// stays safely under the threshold, rather than only budgeting the inter-click gap itself.
    /// This keeps it reliable even on systems with a very short custom-configured double-click
    /// time, where the naive (threshold / N) formula could otherwise leave too little margin
    /// once each click's own hold time is accounted for.
    /// </summary>
    public static void DoubleClick(int x, int y)
    {
        const int clickHoldMs = 10;
        var threshold = (int)GetDoubleClickTime();
        var interClickDelayMs = Math.Max(0, (threshold - 2 * clickHoldMs) / 2);

        SetCursorPos(x, y);
        SendMouseInput(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(clickHoldMs);
        SendMouseInput(MOUSEEVENTF_LEFTUP);

        Thread.Sleep(interClickDelayMs);

        SendMouseInput(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(clickHoldMs);
        SendMouseInput(MOUSEEVENTF_LEFTUP);
    }

    /// <summary>
    /// Synthetic mouse drag from (sourceX, sourceY) to (targetX, targetY): presses the left
    /// button at the source, moves through <paramref name="steps"/> interpolated points spread
    /// across <paramref name="durationMs"/> (some drag targets only recognize a drag if they
    /// observe intermediate mouse-move events rather than an instantaneous jump), then releases
    /// at the target.
    /// </summary>
    public static void Drag(int sourceX, int sourceY, int targetX, int targetY, int steps, int durationMs)
    {
        SetCursorPos(sourceX, sourceY);
        SendMouseInput(MOUSEEVENTF_LEFTDOWN);

        var delayPerStep = steps > 0 ? durationMs / steps : 0;
        for (var i = 1; i <= steps; i++)
        {
            var x = sourceX + (targetX - sourceX) * i / steps;
            var y = sourceY + (targetY - sourceY) * i / steps;
            SetCursorPos(x, y);
            if (delayPerStep > 0)
            {
                Thread.Sleep(delayPerStep);
            }
        }

        SetCursorPos(targetX, targetY);
        SendMouseInput(MOUSEEVENTF_LEFTUP);
    }

    public static void SendText(string text)
    {
        // SendKeys is the simplest reliable way to inject text into a focused control
        // from a console app; escape SendKeys special characters first.
        System.Windows.Forms.SendKeys.SendWait(EscapeSendKeys(text));
    }

    /// <summary>
    /// Sends raw, unescaped SendKeys syntax (e.g. "^a" for Ctrl+A, "{DELETE}", "{ENTER}") to the
    /// currently focused control. Unlike <see cref="SendText"/>, this does NOT escape special
    /// characters — it is the caller's responsibility to pass valid SendKeys syntax. This exists
    /// specifically so key combinations that cannot be expressed as literal text (which SendText
    /// intentionally escapes to prevent) can still be issued.
    /// </summary>
    public static void SendKeysRaw(string keys)
    {
        System.Windows.Forms.SendKeys.SendWait(keys);
    }

    private static string EscapeSendKeys(string text)
    {
        var special = "+^%~(){}[]";
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (special.IndexOf(c) >= 0)
            {
                sb.Append('{').Append(c).Append('}');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static bool IsResponding(IntPtr hwnd, uint timeoutMs)
    {
        var result = SendMessageTimeout(
            hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
            SMTO_ABORTIFHUNG, timeoutMs, out _);
        return result != IntPtr.Zero;
    }
}

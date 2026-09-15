using System.Runtime.InteropServices;

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

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

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

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint GW_OWNER = 4;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint WM_NULL = 0x0000;

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }

    public static void SendText(string text)
    {
        // SendKeys is the simplest reliable way to inject text into a focused control
        // from a console app; escape SendKeys special characters first.
        System.Windows.Forms.SendKeys.SendWait(EscapeSendKeys(text));
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

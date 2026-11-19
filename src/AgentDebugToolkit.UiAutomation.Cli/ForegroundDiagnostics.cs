using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal interface IForegroundDiagnosticSource
{
    IntPtr RootWindow(IntPtr hwnd);
    bool? IsMinimized(IntPtr hwnd);
    IntPtr ForegroundWindow();
    int CurrentSessionId();
    int SessionProtocol(int sessionId);
    int SessionConnectionState(int sessionId);
}

internal static class ForegroundDiagnostics
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static object Attach(IntPtr target, object? extra = null, IForegroundDiagnosticSource? source = null)
    {
        var fields = extra is null ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(extra))!;
        fields["foregroundDiagnostics"] = Capture(target, source ?? new SystemForegroundDiagnosticSource());
        return fields;
    }

    internal static JsonElement Capture(IntPtr target, IForegroundDiagnosticSource source)
    {
        var errors = new List<object>();
        T? Read<T>(string field, Func<T> read) where T : struct
        {
            try { return read(); }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or SecurityException)
            {
                errors.Add(new { field, error = ex.GetType().Name, nativeErrorCode = (ex as Win32Exception)?.NativeErrorCode });
                return null;
            }
        }

        var root = target == IntPtr.Zero ? null : Read("targetRootHwnd", () => source.RootWindow(target));
        bool? minimized = null;
        if (root is { } hwnd && hwnd != IntPtr.Zero)
        {
            var value = Read("isMinimized", () => source.IsMinimized(hwnd) is { } observed
                ? (Known: true, Value: observed) : (Known: false, Value: false));
            if (value is { Known: true } known) minimized = known.Value;
            else if (value is { Known: false }) errors.Add(new { field = "isMinimized", error = "window-gone" });
        }
        else errors.Add(new { field = "targetRootHwnd", error = target == IntPtr.Zero ? "target-unavailable" : "root-unavailable" });

        var foreground = Read("foregroundHwnd", source.ForegroundWindow);
        var session = Read("processSessionId", source.CurrentSessionId);
        var protocol = session is { } id ? Read("sessionProtocolType", () => source.SessionProtocol(id)) : null;
        var state = session is { } sessionId ? Read("sessionConnectionState", () => source.SessionConnectionState(sessionId)) : null;
        var connectionState = state switch
        {
            0 => "active", 1 => "connected", 2 => "connect-query", 3 => "shadow", 4 => "disconnected",
            5 => "idle", 6 => "listen", 7 => "reset", 8 => "down", 9 => "init", _ => "unknown"
        };
        return JsonSerializer.SerializeToElement(new
        {
            targetRootHwnd = root is { } r && r != IntPtr.Zero ? $"0x{r.ToInt64():X}" : null,
            isMinimized = minimized,
            foregroundHwnd = foreground is { } f ? $"0x{f.ToInt64():X}" : null,
            processSessionId = session,
            sessionProtocolType = protocol,
            isRemoteSession = protocol switch { 0 => (bool?)false, 2 => true, _ => null },
            sessionConnectionState = connectionState,
            sessionConnectionStateValue = state,
            remoteClientWindowState = "unknown",
            errors,
            hint = "Read-only, sequential observations of this process's session, not a diagnosis of the foreground failure. " +
                "A local RDP client does not mean this process runs remotely. The remote client's minimized state cannot be inferred. " +
                "No RDP session was changed; RDP restoration is a workaround, not a proven fix."
        }, Options);
    }
}

internal sealed class SystemForegroundDiagnosticSource : IForegroundDiagnosticSource
{
    public IntPtr RootWindow(IntPtr hwnd) => NativeMethods.IsWindow(hwnd)
        ? NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) : IntPtr.Zero;

    public bool? IsMinimized(IntPtr hwnd) => Verbs.ReadMinimizedState(hwnd);
    public IntPtr ForegroundWindow() => NativeMethods.GetForegroundWindow();

    public int CurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    public int SessionProtocol(int sessionId) => Query(sessionId, 16, sizeof(short));
    public int SessionConnectionState(int sessionId) => Query(sessionId, 8, sizeof(int));

    private static int Query(int sessionId, int informationClass, int size)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, informationClass, out var buffer, out var bytes))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (buffer == IntPtr.Zero || bytes < size) throw new Win32Exception(13, "Invalid session observation buffer.");
            return size == sizeof(short) ? (ushort)Marshal.ReadInt16(buffer) : Marshal.ReadInt32(buffer);
        }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int informationClass,
        out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
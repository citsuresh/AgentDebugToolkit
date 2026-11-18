using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace AgentDebugToolkit.Debugger.VisualStudio;

internal sealed record ContextReadError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);

internal static class BreakContextReader
{
    internal static object Read(string mode, Func<object?> instance, Func<int?> thread, Func<string?> frame,
        Func<object?> exceptionLocal, Func<object?> lastBreakpoint, Func<string?> modeAtEnd)
    {
        var errors = new List<ContextReadError>();
        T? Observe<T>(string field, Func<T?> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is COMException or ComBusyRetryExhaustedException)
            {
                errors.Add(new(field, ex is ComBusyRetryExhaustedException ? "com-busy-retry-exhausted" : "context-read-failed", ex.Message));
                return default;
            }
        }
        var identity = Observe("instance", instance);
        var selectedThreadId = mode == "break" ? Observe("selectedThreadId", thread) : null;
        var selectedFrameFunction = mode == "break" ? Observe("selectedFrameFunction", frame) : null;
        var exposedException = mode == "break" ? Observe("exceptionLocal", exceptionLocal) : null;
        var breakpoint = mode == "break" ? Observe("lastBreakpointHit", lastBreakpoint) : null;
        return new
        {
            instance = identity, breakReason = "unknown", selectedThreadId, selectedFrameFunction,
            exceptionLocal = exposedException, lastBreakpointHit = breakpoint,
            modeAtEnd = Observe("modeAtEnd", modeAtEnd), observationsAreAtomic = false, errors,
            hint = "The selected frame, an exposed $exception local, and BreakpointLastHit are observations, not proof of the current stop cause or current source location."
        };
    }
}
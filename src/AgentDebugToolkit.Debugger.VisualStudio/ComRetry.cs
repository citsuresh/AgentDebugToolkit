using System.Runtime.InteropServices;

namespace AgentDebugToolkit.Debugger.VisualStudio;

/// <summary>
/// Retries EnvDTE/COM calls that fail because Visual Studio's message filter is temporarily
/// busy (e.g. mid-build, mid-launch, or servicing another cross-process call) instead of
/// letting the failure bubble up as a generic unhandled exception. This is a recurring interop
/// hazard for any out-of-process automation of devenv.exe, not specific to one verb.
/// </summary>
internal static class ComRetry
{
    private const int RpcServerCallRetryLater = unchecked((int)0x8001010A);
    private const int RpcCallRejected = unchecked((int)0x80010001);
    private static readonly int[] BackoffMs = [100, 200, 300];

    public static T Invoke<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException ex) when (IsBusy(ex))
            {
                if (attempt >= BackoffMs.Length)
                {
                    throw new ComBusyRetryExhaustedException(ex);
                }

                Thread.Sleep(BackoffMs[attempt]);
            }
        }
    }

    public static void Invoke(Action action) => Invoke<object?>(() =>
    {
        action();
        return null;
    });

    private static bool IsBusy(COMException ex) =>
        ex.ErrorCode == RpcServerCallRetryLater || ex.ErrorCode == RpcCallRejected;
}

/// <summary>
/// Thrown when a COM call to Visual Studio kept failing as "busy" after all retry attempts were
/// exhausted; distinct from a generic unhandled exception so callers can surface a specific,
/// actionable error code (see docs/CLI_CONTRACT.md's <c>com-busy-retry-exhausted</c> entry).
/// </summary>
internal sealed class ComBusyRetryExhaustedException(Exception inner)
    : Exception($"Visual Studio's COM message filter reported busy repeatedly: {inner.Message}", inner);

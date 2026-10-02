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
    private const int RpcServerCallRetryLater = unchecked((int)0x8001010A); // RPC_E_SERVERCALL_RETRYLATER
    private const int RpcCallRejected = unchecked((int)0x80010001); // RPC_E_CALL_REJECTED
    // Additional transient "VS message filter is busy" HRESULTs observed around debugger
    // state-reporting calls (CurrentMode, BreakpointLastHit, etc.), alongside the two above;
    // same retry-with-backoff treatment applies to all four.
    private const int RpcServerCallRejected = unchecked((int)0x8001010C); // RPC_E_SERVERCALL_REJECTED
    private const int RpcCallComplete = unchecked((int)0x80010117); // RPC_E_CALL_COMPLETE
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

    /// <summary>
    /// Iterates an EnvDTE/COM collection using explicit, individually-retried
    /// <c>GetEnumerator()</c>/<c>MoveNext()</c>/<c>Current</c> calls instead of a plain C#
    /// <c>foreach</c>. A `foreach` over a COM collection has the compiler generate its own
    /// <c>GetEnumerator()</c> call and loop condition implicitly -- those are themselves
    /// cross-process COM calls into devenv that can throw the same busy HRESULTs this class
    /// retries elsewhere, but because `foreach` never routes them through <see cref="Invoke"/>,
    /// a busy enumerator acquisition/advance bypasses the retry/backoff path entirely and leaks
    /// as a raw, unhandled <see cref="COMException"/> instead of the intended
    /// <see cref="ComBusyRetryExhaustedException"/>/<c>com-busy-retry-exhausted</c> result.
    /// Confirmed live via concurrent stress testing (see docs/KNOWN_OPEN_FINDINGS.md) -- every
    /// `foreach` over an EnvDTE-returned collection in this project should use this helper
    /// instead.
    /// </summary>
    public static void ForEach<T>(System.Collections.IEnumerable collection, Action<T> action) =>
        ForEach<T>(collection, item =>
        {
            action(item);
            return true;
        });

    /// <summary>
    /// Same as <see cref="ForEach{T}(System.Collections.IEnumerable, Action{T})"/>, but stops
    /// enumerating as soon as <paramref name="action"/> returns <see langword="false"/> (e.g. once
    /// a matching item has been found) instead of always exhausting the collection.
    /// </summary>
    public static void ForEach<T>(System.Collections.IEnumerable collection, Func<T, bool> action)
    {
        var enumerator = Invoke(collection.GetEnumerator);
        try
        {
            while (Invoke(enumerator.MoveNext))
            {
                var current = Invoke(() => enumerator.Current);
                if (!action((T)current!))
                {
                    break;
                }
            }
        }
        finally
        {
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static bool IsBusy(COMException ex) =>
        ex.ErrorCode == RpcServerCallRetryLater || ex.ErrorCode == RpcCallRejected
        || ex.ErrorCode == RpcServerCallRejected || ex.ErrorCode == RpcCallComplete;
}

/// <summary>
/// Thrown when a COM call to Visual Studio kept failing as "busy" after all retry attempts were
/// exhausted; distinct from a generic unhandled exception so callers can surface a specific,
/// actionable error code (see docs/CLI_CONTRACT.md's <c>com-busy-retry-exhausted</c> entry).
/// </summary>
internal sealed class ComBusyRetryExhaustedException(Exception inner)
    : Exception($"Visual Studio's COM message filter reported busy repeatedly: {inner.Message}", inner);

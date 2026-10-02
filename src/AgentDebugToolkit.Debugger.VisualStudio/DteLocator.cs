using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using EnvDTE;

namespace AgentDebugToolkit.Debugger.VisualStudio;

/// <summary>
/// Locates a running `devenv.exe` instance's DTE object via the Running Object Table (ROT).
/// A running Visual Studio process registers itself in the ROT under a moniker like
/// "!VisualStudio.DTE.17.0:1234" (pid suffix); there is no other supported way to attach to an
/// already-running instance from an external process.
/// </summary>
internal static class DteLocator
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    public static (DTE? dte, string? errorCode, string? error) FindDte(string? solutionNameFilter)
    {
        List<(DTE dte, string moniker)> candidates;
        try
        {
            candidates = EnumerateDteInstances();
        }
        catch (RotUnavailableException)
        {
            return (null, "rot-unavailable", "Could not access the Running Object Table.");
        }

        if (candidates.Count == 0)
        {
            return (null, "devenv-not-found", "No running Visual Studio instances were found.");
        }

        if (string.IsNullOrEmpty(solutionNameFilter))
        {
            return (candidates[0].dte, null, null);
        }

        foreach (var (dte, _) in candidates)
        {
            string? solutionFile = null;
            try
            {
                solutionFile = ComRetry.Invoke(() => dte.Solution?.FullName);
            }
            catch (COMException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(solutionFile)
                && Path.GetFileNameWithoutExtension(solutionFile)
                    .Equals(solutionNameFilter, StringComparison.OrdinalIgnoreCase))
            {
                return (dte, null, null);
            }
        }

        return (null, "devenv-not-found",
            $"No running Visual Studio instance has a solution named '{solutionNameFilter}' open.");
    }

    /// <summary>
    /// Scans every running `devenv.exe` instance's <c>Debugger.LocalProcesses</c> (the set of
    /// processes that instance could attach to, matching the examples in EnvDTE's own
    /// <c>Process.Attach</c> documentation) for one whose <c>ProcessID</c> equals <paramref
    /// name="targetPid"/>. Unlike <see cref="FindDte"/>'s "first instance wins" default, this
    /// never guesses: a PID visible to more than one instance (or to none) is reported as an
    /// explicit error with full candidate detail rather than silently picking one, per this
    /// toolkit's "ask, don't guess" convention for ambiguous targets (see
    /// `ambiguous-process`/`ambiguous-window` in docs/CLI_CONTRACT.md).
    /// </summary>
    public static (DTE? dte, Process? process, string? errorCode, string? error, object? candidates)
        FindProcessToAttach(int targetPid, string? solutionNameFilter = null) =>
        FindProcessByPid(targetPid, solutionNameFilter, dte => ComRetry.Invoke(() => dte.Debugger.LocalProcesses),
            "process-not-found",
            count => $"No running Visual Studio instance (scanned {count}) can debug process {targetPid}. " +
                "Confirm the PID is correct and the process is running on this machine.",
            count => $"Process {targetPid} is attachable from {count} running Visual Studio instances; " +
                "specify --solution to disambiguate.");

    /// <summary>
    /// Same as <see cref="FindProcessToAttach"/>, but scans <c>Debugger.DebuggedProcesses</c>
    /// (the set of processes a given instance is currently debugging) instead of
    /// <c>LocalProcesses</c> (every attachable process on the machine) — used by
    /// <c>break-all</c>/<c>detach</c>, which require the target PID to already be under an
    /// active debug session rather than merely attachable.
    /// </summary>
    public static (DTE? dte, Process? process, string? errorCode, string? error, object? candidates)
        FindDebuggedProcess(int targetPid, string? solutionNameFilter = null) =>
        FindProcessByPid(targetPid, solutionNameFilter, dte => ComRetry.Invoke(() => dte.Debugger.DebuggedProcesses),
            "process-not-debugged",
            count => $"No running Visual Studio instance (scanned {count}) is currently debugging process {targetPid}.",
            count => $"Process {targetPid} is being debugged by {count} running Visual Studio instances; " +
                "specify --solution to disambiguate.");

    private static (DTE? dte, Process? process, string? errorCode, string? error, object? candidates)
        FindProcessByPid(
            int targetPid,
            string? solutionNameFilter,
            Func<DTE, Processes?> getProcesses,
            string notFoundErrorCode,
            Func<int, string> notFoundMessage,
            Func<int, string> ambiguousMessage)
    {
        List<(DTE dte, string moniker)> instances;
        try
        {
            instances = EnumerateDteInstances();
        }
        catch (RotUnavailableException)
        {
            return (null, null, "rot-unavailable", "Could not access the Running Object Table.", null);
        }

        if (instances.Count == 0)
        {
            return (null, null, "devenv-not-found", "No running Visual Studio instances were found.", null);
        }

        if (!string.IsNullOrEmpty(solutionNameFilter))
        {
            instances = instances
                .Where(i => string.Equals(TryGetSolutionName(i.dte), solutionNameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (instances.Count == 0)
            {
                return (null, null, "devenv-not-found",
                    $"No running Visual Studio instance has a solution named '{solutionNameFilter}' open.", null);
            }
        }

        var matches = new List<(DTE dte, string moniker, Process process)>();
        foreach (var (dte, moniker) in instances)
        {
            Processes? processes;
            try
            {
                processes = getProcesses(dte);
            }
            catch (COMException)
            {
                // Instance may be busy/starting up; skip it rather than failing the whole scan,
                // consistent with FindDte's handling of per-instance COM failures above.
                continue;
            }

            if (processes is null)
            {
                continue;
            }

            try
            {
                var matchedThisInstance = false;
                ComRetry.ForEach<Process>(processes, process =>
                {
                    if (!matchedThisInstance && ComRetry.Invoke(() => process.ProcessID) == targetPid)
                    {
                        matches.Add((dte, moniker, process));
                        matchedThisInstance = true;
                        // Keep iterating (not releasing the loop) only to release the remaining
                        // RCWs below; the match itself is already captured.
                        return;
                    }

                    Marshal.ReleaseComObject(process);
                });
            }
            finally
            {
                Marshal.ReleaseComObject(processes);
            }
        }

        if (matches.Count == 0)
        {
            return (null, null, notFoundErrorCode, notFoundMessage(instances.Count), null);
        }

        if (matches.Count > 1)
        {
            var candidateList = matches
                .Select(m => new { instance = m.moniker, solution = TryGetSolutionName(m.dte) })
                .ToList();
            foreach (var (_, _, process) in matches)
            {
                Marshal.ReleaseComObject(process);
            }

            return (null, null, "ambiguous-process", ambiguousMessage(matches.Count), candidateList);
        }

        var (selectedDte, _, selectedProcess) = matches[0];
        return (selectedDte, selectedProcess, null, null, null);
    }

    private static string? TryGetSolutionName(DTE dte)
    {
        try
        {
            var solutionFile = ComRetry.Invoke(() => dte.Solution?.FullName);
            return string.IsNullOrEmpty(solutionFile) ? null : Path.GetFileNameWithoutExtension(solutionFile);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static List<(DTE dte, string moniker)> EnumerateDteInstances()
    {
        var candidates = new List<(DTE dte, string moniker)>();

        var hr = GetRunningObjectTable(0, out var rot);
        if (hr != 0 || rot is null)
        {
            throw new RotUnavailableException();
        }

        try
        {
            // rot.EnumRunning itself is a cross-process COM call into Visual Studio's ROT
            // registration machinery and can legitimately fail with the same "busy" HRESULTs as
            // any other devenv-bound COM call under contention (confirmed live via concurrent CLI
            // invocations) -- wrap it through ComRetry like every other devenv-bound call in this
            // file, rather than leaving it as a bare call that bypasses the shared retry/backoff
            // path and surfaces as a raw unhandled-exception instead of com-busy-retry-exhausted.
            var enumMoniker = ComRetry.Invoke(() =>
            {
                rot.EnumRunning(out var e);
                return e;
            });
            if (enumMoniker is null)
            {
                return candidates;
            }

            try
            {
                ComRetry.Invoke(() => enumMoniker.Reset());
                var monikers = new IMoniker[1];
                var fetchedPtr = Marshal.AllocHGlobal(sizeof(int));
                CreateBindCtx(0, out var bindCtx);

                try
                {
                    while (ComRetry.Invoke(() => enumMoniker.Next(1, monikers, fetchedPtr)) == 0 && Marshal.ReadInt32(fetchedPtr) == 1)
                    {
                        var moniker = monikers[0];
                        try
                        {
                            string displayName;
                            try
                            {
                                displayName = ComRetry.Invoke(() =>
                                {
                                    moniker.GetDisplayName(bindCtx, null, out var name);
                                    return name;
                                });
                            }
                            catch (COMException)
                            {
                                continue;
                            }

                            if (!displayName.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            try
                            {
                                var comObject = ComRetry.Invoke(() =>
                                {
                                    rot.GetObject(moniker, out var obj);
                                    return obj;
                                });
                                if (comObject is DTE dte)
                                {
                                    candidates.Add((dte, displayName));
                                }
                            }
                            catch (COMException)
                            {
                                // The instance may be busy/starting up; skip it rather than failing the whole scan.
                            }
                        }
                        finally
                        {
                            // Only the enumeration moniker RCW is released here -- comObject/dte
                            // above is intentionally NOT released: it is returned to (or held by)
                            // the caller via `candidates`, and callers dispose/use it independently.
                            Marshal.ReleaseComObject(moniker);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(fetchedPtr);
                    if (bindCtx is not null)
                    {
                        Marshal.ReleaseComObject(bindCtx);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumMoniker);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rot);
        }

        return candidates;
    }
}

/// <summary>
/// Thrown internally by <see cref="DteLocator.EnumerateDteInstances"/> when the Running Object
/// Table itself could not be accessed, so both <see cref="DteLocator.FindDte"/> and <see
/// cref="DteLocator.FindProcessToAttach"/> can surface the same <c>rot-unavailable</c> error
/// without duplicating the `GetRunningObjectTable` failure check in each caller.
/// </summary>
internal sealed class RotUnavailableException : Exception;

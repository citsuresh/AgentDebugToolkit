using System.IO;
using System.Text.Json;
using System.Threading;

namespace AgentDebugToolkit.UiAutomation.Cli;

/// <summary>
/// Thrown by <see cref="SessionContext.Save"/> when the final <see cref="File.Move(string, string, bool)"/>
/// attempt still fails after exhausting its retry-with-backoff loop. Callers should catch this specifically
/// and report a dedicated error code (e.g. "session-context-write-failed") instead of letting it surface as
/// a generic unhandled-exception. See docs/KNOWN_OPEN_FINDINGS.md (Phase 15) for the underlying
/// UnauthorizedAccessException race this addresses.
/// </summary>
public sealed class SessionContextWriteException : Exception
{
    public SessionContextWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Persists the "current" pid across CLI invocations (each invocation is a fresh process).
/// See docs/ARCHITECTURE.md "Process/session model".
/// </summary>
public static class SessionContext
{
    private static readonly string FilePath = Path.Combine(
        Path.GetTempPath(), "agentdebugtoolkit", "ui-session.json");

    // Phase 15: File.Move onto an existing target can intermittently throw
    // UnauthorizedAccessException even with a single writer thread (observed 66-201 failures per
    // 2000 iterations in stress testing) -- likely a transient AV/indexer lock. Retry with a short
    // exponential backoff before giving up: 20/40/80/160/320ms (~630ms worst case), cheap enough
    // for an interactive CLI call. Deliberately scoped to UnauthorizedAccessException only (not
    // the broader IOException hierarchy) -- that is the specific, observed transient failure;
    // other IOException subtypes (e.g. disk-full, path-not-found) would not be helped by retrying
    // and should fail fast with their real exception surfaced instead of being masked here.
    private const int MaxMoveAttempts = 5;
    private const int InitialBackoffMs = 20;

    public record ContextData(int Pid, string ProcessName, DateTime StartedAtUtc);

    public static void Save(int pid, string processName, DateTime startedAtUtc)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var data = new ContextData(pid, processName, startedAtUtc);
        var temporaryPath = Path.Combine(dir, $".ui-session-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data));
            MoveWithRetry(temporaryPath, FilePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void MoveWithRetry(string sourcePath, string destinationPath)
    {
        var backoffMs = InitialBackoffMs;
        for (var attempt = 1; attempt <= MaxMoveAttempts; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < MaxMoveAttempts)
            {
                Thread.Sleep(backoffMs);
                backoffMs *= 2;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new SessionContextWriteException(
                    $"Failed to write session context after {MaxMoveAttempts} attempts: {ex.Message}",
                    ex);
            }
        }
    }

    public static ContextData? Load()
    {
        try
        {
            using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ContextData>(stream);
        }
        catch
        {
            return null;
        }
    }
}

using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using static AgentDebugToolkit.ConsoleAutomation.Cli.NativeConPty;

namespace AgentDebugToolkit.ConsoleAutomation.Cli;

internal static class BrokerProgram
{
    private const int RequestTimeoutMs = 3000;
    private const int MaxRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions PipeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        if (!options.TryGetValue("sessionId", out var sessionId)
            || !options.TryGetValue("exe", out var executable))
        {
            return 2;
        }

        var columns = ParsePositive(options, "cols", 120);
        var rows = ParsePositive(options, "rows", 30);
        var pipeName = $"agentdebugtoolkit-console-{sessionId}";
        using var session = new ConPtySession(executable, options.GetValueOrDefault("args", ""), columns, rows);
        var buffer = new TerminalBuffer(columns, rows);
        using var outputDrained = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            try
            {
                CopyOutput(session.Output, buffer);
            }
            finally
            {
                outputDrained.Set();
            }
        })
        {
            IsBackground = true
        };
        reader.Start();
        var exitWatcher = new Thread(() => session.ClosePseudoConsoleWhenTargetExits(outputDrained))
        {
            IsBackground = true
        };
        exitWatcher.Start();
        using var input = new InputDispatcher(session.Input);
        Serve(pipeName, session, input, buffer, outputDrained);
        exitWatcher.Join();
        reader.Join();
        return 0;
    }

    private static void Serve(
        string pipeName,
        ConPtySession session,
        InputDispatcher input,
        TerminalBuffer buffer,
        ManualResetEventSlim outputDrained)
    {
        using var shutdown = new CancellationTokenSource();
        while (!shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(shutdown.Token).GetAwaiter().GetResult();
                var connectedPipe = pipe;
                _ = Task.Run(() => ServeConnectionAsync(connectedPipe, session, input, buffer, outputDrained, shutdown));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
            }
            catch (IOException)
            {
                pipe?.Dispose();
                // All instances can be briefly occupied while timed-out clients are being
                // disposed. Retry the listener instead of letting that transient condition
                // terminate the broker process.
                Thread.Sleep(50);
            }
        }
    }

    private static async Task ServeConnectionAsync(
        NamedPipeServerStream pipe,
        ConPtySession session,
        InputDispatcher input,
        TerminalBuffer buffer,
        ManualResetEventSlim outputDrained,
        CancellationTokenSource shutdown)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            })
            {
                using var requestTimeout = new CancellationTokenSource(RequestTimeoutMs);
                var line = await ReadRequestAsync(pipe, requestTimeout.Token);
                if (line is null) return;

                var request = JsonSerializer.Deserialize<BrokerRequest>(line, PipeJsonOptions);
                var response = Handle(request, session, input, buffer, outputDrained);
                var isShutdownRequest = request?.Command == "shutdown";
                try
                {
                    writer.WriteLine(JsonSerializer.Serialize(response));
                }
                finally
                {
                    if (isShutdownRequest)
                    {
                        shutdown.Cancel();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Invalid input affects this connection only; the next pipe instance remains available.
        }
        catch (OperationCanceledException)
        {
            // A newline-less request exceeded its bounded read window; disposing this connection
            // releases the server instance for a later client.
        }
        catch (IOException)
        {
            // A single client disconnected or broke the pipe. Other pipe instances keep serving.
        }
    }

    private static async Task<string?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var request = new List<byte>();
        var buffer = new byte[1];
        while (request.Count < MaxRequestBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                // The protocol is line-delimited. EOF without a newline is an incomplete
                // request, never a command to deserialize and execute.
                return null;
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(request.ToArray()).TrimEnd('\r');
            }

            request.Add(buffer[0]);
        }

        throw new JsonException($"Broker request exceeds {MaxRequestBytes} bytes.");
    }

    private static object Handle(
        BrokerRequest? request,
        ConPtySession session,
        InputDispatcher input,
        TerminalBuffer buffer,
        ManualResetEventSlim outputDrained) =>
        request?.Command switch
        {
            "get-screen-snapshot" => GetScreenSnapshot(session, buffer, outputDrained, request.Lines),
            "send-text" => SendText(session, input, request.Text),
            "get-session-info" => new { success = true, pid = session.ProcessId },
            "is-running" => GetRunningStatus(session, outputDrained),
            "shutdown" => Shutdown(session),
            _ => new { success = false, error = "invalid-argument", message = "Unknown broker command." }
        };

    private static object GetScreenSnapshot(
        ConPtySession session,
        TerminalBuffer buffer,
        ManualResetEventSlim outputDrained,
        int? lines)
    {
        if (!session.IsRunning)
        {
            outputDrained.Wait();
        }

        return new { success = true, lines = buffer.Snapshot(lines) };
    }

    private static object GetRunningStatus(ConPtySession session, ManualResetEventSlim outputDrained)
    {
        var running = session.IsRunning;
        if (!running)
        {
            outputDrained.Wait();
        }

        return new { success = true, running, exitCode = running ? (int?)null : session.ExitCode };
    }

    private static object SendText(ConPtySession session, InputDispatcher input, string? text)
    {
        if (text is null) return new { success = false, error = "invalid-argument", message = "Text is required." };
        if (!session.IsRunning)
        {
            return new
            {
                success = false,
                error = "target-exited",
                message = $"The target process exited with code {session.ExitCode}."
            };
        }

        if (!input.TryQueue(Encoding.UTF8.GetBytes(text)))
        {
            return new
            {
                success = false,
                error = "input-busy",
                message = "The console input queue is full."
            };
        }

        return new { success = true };
    }

    private static object Shutdown(ConPtySession session)
    {
        if (session.IsRunning)
        {
            try
            {
                session.Terminate();
            }
            catch (System.ComponentModel.Win32Exception ex)
                when (ex.NativeErrorCode == NativeConPty.ErrorAccessDenied && !session.IsRunning)
            {
                // The target exited after the liveness check. Shutdown is still complete.
            }
        }
        return new { success = true };
    }

    private static void CopyOutput(Stream output, TerminalBuffer buffer)
    {
        using var reader = new StreamReader(output, Encoding.UTF8);
        var characters = new char[1024];
        int read;
        while ((read = reader.Read(characters, 0, characters.Length)) > 0)
        {
            buffer.Write(new string(characters, 0, read));
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (args[index].StartsWith("--")) result[args[index][2..]] = args[index + 1];
        }
        return result;
    }

    private static int ParsePositive(Dictionary<string, string> options, string key, int defaultValue) =>
        options.TryGetValue(key, out var value) && int.TryParse(value, out var result) && result > 0
            ? result : defaultValue;

    private sealed record BrokerRequest(string Command, string? Text, int? Lines);

    private sealed class InputDispatcher : IDisposable
    {
        private const int QueueCapacity = 16;
        private readonly Stream input;
        private readonly BlockingCollection<byte[]> pending = new(QueueCapacity);
        private readonly Thread worker;

        public InputDispatcher(Stream input)
        {
            this.input = input;
            worker = new Thread(WritePendingInput)
            {
                IsBackground = true
            };
            worker.Start();
        }

        public bool TryQueue(byte[] text) => pending.TryAdd(text);

        public void Dispose()
        {
            pending.CompleteAdding();
            if (!worker.Join(5000))
            {
                input.Dispose();
                worker.Join();
            }

            pending.Dispose();
        }

        private void WritePendingInput()
        {
            try
            {
                foreach (var text in pending.GetConsumingEnumerable())
                {
                    input.Write(text);
                    input.Flush();
                }
            }
            catch (IOException)
            {
                pending.CompleteAdding();
            }
        }
    }

    private sealed class ConPtySession : IDisposable
    {
        private IntPtr pseudoConsole;
        private readonly FileStream input;
        private readonly FileStream output;

        public ConPtySession(string executable, string arguments, int columns, int rows)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("Target executable was not found.", executable);
            CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0).ThrowOnFailure();
            CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0).ThrowOnFailure();
            var result = CreatePseudoConsole(new NativeConPty.Coord { X = (short)columns, Y = (short)rows },
                inputRead, outputWrite, 0, out pseudoConsole);
            if (result != 0)
            {
                inputRead.Dispose();
                outputWrite.Dispose();
                throw new System.ComponentModel.Win32Exception(result, "CreatePseudoConsole failed.");
            }
            input = new FileStream(inputWrite, FileAccess.Write);
            output = new FileStream(outputRead, FileAccess.Read);

            nuint attributeSize = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            var attributeList = Marshal.AllocHGlobal(checked((nint)attributeSize));
            try
            {
                InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize).ThrowOnFailure();
                UpdateProcThreadAttribute(attributeList, 0, (nuint)NativeConPty.ProcThreadAttributePseudoConsole,
                    pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero).ThrowOnFailure();
                var startup = new NativeConPty.StartupInfoEx
                {
                    StartupInfo = new NativeConPty.StartupInfo
                    {
                        cb = checked((uint)Marshal.SizeOf<NativeConPty.StartupInfoEx>()),
                        dwFlags = NativeConPty.StartfUseStdHandles
                    },
                    AttributeList = attributeList
                };
                // CreateProcessW is allowed to modify lpCommandLine, so this must remain a
                // mutable buffer for the duration of the native call.
                var commandLine = new StringBuilder($"\"{executable}\" {arguments}");
                CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    NativeConPty.ExtendedStartupInfoPresent | NativeConPty.CreateUnicodeEnvironment,
                    IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info).ThrowOnFailure();
                processHandle = info.Process;
                ProcessId = checked((int)info.ProcessId);
                CloseHandle(info.Thread);
            }
            finally
            {
                inputRead.Dispose();
                outputWrite.Dispose();
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }

        private readonly IntPtr processHandle;
        public int ProcessId { get; }
        public Stream Input => input;
        public Stream Output => output;
        public bool IsRunning
        {
            get
            {
                var waitResult = WaitForSingleObject(processHandle, 0);
                return waitResult switch
                {
                    WaitTimeout => true,
                    WaitObject0 => false,
                    WaitFailed => throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(), "WaitForSingleObject failed while checking target liveness."),
                    _ => throw new InvalidOperationException(
                        $"WaitForSingleObject returned unexpected result 0x{waitResult:X8}.")
                };
            }
        }

        public int ExitCode
        {
            get
            {
                if (IsRunning)
                {
                    throw new InvalidOperationException("Exit code is unavailable while the target process is running.");
                }

                GetExitCodeProcess(processHandle, out var exitCode).ThrowOnFailure();
                return unchecked((int)exitCode);
            }
        }

        public void Terminate() => TerminateProcess(processHandle, 1).ThrowOnFailure();

        public void ClosePseudoConsoleWhenTargetExits(ManualResetEventSlim outputDrained)
        {
            if (WaitForSingleObject(processHandle, Infinite) == WaitObject0)
            {
                ClosePseudoConsoleOnce();
                outputDrained.Wait();
            }
        }

        public void Dispose()
        {
            input.Dispose();
            output.Dispose();
            ClosePseudoConsoleOnce();
            CloseHandle(processHandle);
        }

        private void ClosePseudoConsoleOnce()
        {
            var handle = Interlocked.Exchange(ref pseudoConsole, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                ClosePseudoConsole(handle);
            }
        }
    }

    private static void ThrowOnFailure(this bool result)
    {
        if (!result) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}

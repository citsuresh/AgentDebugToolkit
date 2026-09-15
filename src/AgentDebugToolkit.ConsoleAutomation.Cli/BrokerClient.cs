using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgentDebugToolkit.ConsoleAutomation.Cli;

internal static class BrokerClient
{
    public static JsonDocument SendRequest(string pipeName, object request, int connectTimeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(connectTimeoutMs);
        try
        {
            return SendRequestAsync(pipeName, request, timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The broker did not respond within {connectTimeoutMs}ms.");
        }
    }

    public static async Task<JsonDocument> SendRequestAsync(
        string pipeName,
        object request,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        await pipe.ConnectAsync(cancellationToken);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
        var response = await reader.ReadLineAsync(cancellationToken);
        if (response is null)
        {
            throw new IOException("The broker closed the pipe without a response.");
        }

        return JsonDocument.Parse(response);
    }
}

namespace BicepRpc;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

public class JsonRpcClient(
    Stream reader,
    Stream writer) : IAsyncDisposable
{
    private record JsonRpcRequest<T>(
        string Jsonrpc,
        string Method,
        T Params,
        int Id);

    private record MinimalJsonRpcResponse(
        string Jsonrpc,
        int Id);

    private record JsonRpcResponse<T>(
        string Jsonrpc,
        T? Result,
        JsonRpcError? Error,
        int Id);

    private record JsonRpcError(
        string Code,
        string Message);

    private readonly byte[] terminator = "\r\n\r\n"u8.ToArray();
    private int nextId = 0;
    private readonly SemaphoreSlim writeSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> pendingResponses = new();

    private readonly JsonSerializerOptions jsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<TResponse> SendRequest<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken)
    {
        var currentId = Interlocked.Increment(ref nextId);

        var jsonRpcRequest = new JsonRpcRequest<TRequest>(Jsonrpc: "2.0", Method: method, Params: request, Id: currentId);
        var requestContent = JsonSerializer.Serialize(jsonRpcRequest, jsonSerializerOptions);
        var requestLength = Encoding.UTF8.GetByteCount(requestContent);
        var rawRequest = $"Content-Length: {requestLength}\r\n\r\n{requestContent}";
        var requestBytes = Encoding.UTF8.GetBytes(rawRequest);

        await writeSemaphore.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteAsync(requestBytes, cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            writeSemaphore.Release();
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingResponses.TryAdd(currentId, tcs))
        {
            throw new InvalidOperationException($"A request with ID {currentId} is already pending.");
        }

        var responseContent = await tcs.Task.WaitAsync(cancellationToken);
        var jsonRpcResponse = JsonSerializer.Deserialize<JsonRpcResponse<TResponse>>(responseContent, jsonSerializerOptions)
            ?? throw new InvalidOperationException("Failed to deserialize JSON-RPC response");

        if (jsonRpcResponse.Result is null)
        {
            var error = jsonRpcResponse.Error ?? new JsonRpcError("UnknownError", "Unknown error");
            throw new InvalidOperationException($"JSON-RPC request failed with error code {error.Code}: {error.Message}");
        }
        
        return jsonRpcResponse.Result;
    }

    public async Task ReadLoop(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadMessage(cancellationToken);
            try
            {
                var response = JsonSerializer.Deserialize<MinimalJsonRpcResponse>(message, jsonSerializerOptions)
                    ?? throw new InvalidOperationException("Failed to deserialize JSON-RPC response");

                if (pendingResponses.TryRemove(response.Id, out var tcs))
                {
                    tcs.SetResult(message);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private async Task<string> ReadUntilTerminator(CancellationToken cancellationToken)
    {
        using var outputStream = new MemoryStream();
        var patternIndex = 0;
        var byteBuffer = new byte[1].AsMemory();

        while (true)
        {
            await reader.ReadExactlyAsync(byteBuffer, cancellationToken);

            await outputStream.WriteAsync(byteBuffer, cancellationToken);
            patternIndex = terminator[patternIndex] == byteBuffer.Span[0] ? patternIndex + 1 : 0;
            if (patternIndex == terminator.Length)
            {
                outputStream.Position = 0;
                outputStream.SetLength(outputStream.Length - terminator.Length);
                // return stream as string
                return Encoding.UTF8.GetString(outputStream.ToArray());
            }
        }
    }

    private async Task<string> ReadContent(int length, CancellationToken cancellationToken)
    {
        var byteBuffer = new byte[length].AsMemory();
        await reader.ReadExactlyAsync(byteBuffer, cancellationToken);

        return Encoding.UTF8.GetString(byteBuffer.Span);
    }

    private async Task<string> ReadMessage(CancellationToken cancellationToken)
    {
        var header = await ReadUntilTerminator(cancellationToken);
        var parsed = header.Split(":", StringSplitOptions.TrimEntries);

        if (parsed.Length != 2 ||
            !parsed[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(parsed[1], out var contentLength) ||
            contentLength <= 0)
        {
            throw new InvalidOperationException($"Invalid header: {header}");
        }

        return await ReadContent(contentLength, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await writer.DisposeAsync();
        await reader.DisposeAsync();
    }
}
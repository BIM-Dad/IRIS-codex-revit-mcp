using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.UI;

namespace Iris.RevitMcp;

public sealed class PipeBridgeService : IDisposable
{
    public const string PipeName = "IRIS.RevitMcpBridge.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RevitRequestExternalEventHandler _handler;
    private readonly ExternalEvent _externalEvent;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public PipeBridgeService(RevitRequestExternalEventHandler handler, ExternalEvent externalEvent)
    {
        _handler = handler;
        _externalEvent = externalEvent;
    }

    public void Start()
    {
        _listenTask = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(500, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure(null, "Empty request."), JsonOptions)).ConfigureAwait(false);
            return;
        }

        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions);
        }
        catch (JsonException ex)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure(null, $"Invalid JSON: {ex.Message}"), JsonOptions)).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Tool))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure(null, "Missing tool name."), JsonOptions)).ConfigureAwait(false);
            return;
        }

        var workItem = new BridgeWorkItem(request);
        _handler.Enqueue(workItem);
        _externalEvent.Raise();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        BridgeResponse response;
        try
        {
            response = await workItem.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            response = BridgeResponse.Failure(null, "Timed out waiting for Revit to process the request.");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed class RevitRequestExternalEventHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<BridgeWorkItem> _queue = new();

    public void Enqueue(BridgeWorkItem workItem)
    {
        _queue.Enqueue(workItem);
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                item.Completion.TrySetResult(RevitToolDispatcher.Execute(app, item.Request));
            }
            catch (Exception ex)
            {
                var documentName = app.ActiveUIDocument?.Document?.Title;
                item.Completion.TrySetResult(BridgeResponse.Failure(documentName, ex.Message));
            }
        }
    }

    public string GetName()
    {
        return "IRIS Revit MCP Request Handler";
    }
}

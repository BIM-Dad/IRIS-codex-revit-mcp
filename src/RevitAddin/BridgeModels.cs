using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.RevitMcp;

public sealed class BridgeRequest
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}

public sealed class BridgeResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("documentName")]
    public string? DocumentName { get; init; }

    [JsonPropertyName("result")]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static BridgeResponse Success(string? documentName, object result)
    {
        return new BridgeResponse { Ok = true, DocumentName = documentName, Result = result };
    }

    public static BridgeResponse Failure(string? documentName, string error)
    {
        return new BridgeResponse { Ok = false, DocumentName = documentName, Error = error };
    }
}

public sealed class BridgeWorkItem
{
    public BridgeWorkItem(BridgeRequest request)
    {
        Request = request;
    }

    public BridgeRequest Request { get; }

    public TaskCompletionSource<BridgeResponse> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

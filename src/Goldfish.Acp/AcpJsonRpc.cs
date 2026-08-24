using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goldfish.Acp;

public sealed record AcpJsonRpcRequest<TParams>(
    [property: JsonPropertyName("id")] object? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] TParams Params)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = AcpConstants.JsonRpcVersion;
}

public sealed record AcpJsonRpcNotification<TParams>(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] TParams Params)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = AcpConstants.JsonRpcVersion;
}

public sealed record AcpJsonRpcResponse<TResult>(
    [property: JsonPropertyName("id")] object? Id,
    [property: JsonPropertyName("result")] TResult Result)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = AcpConstants.JsonRpcVersion;
}

public sealed record AcpJsonRpcErrorResponse(
    [property: JsonPropertyName("id")] object? Id,
    [property: JsonPropertyName("error")] AcpJsonRpcError Error)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = AcpConstants.JsonRpcVersion;
}

public sealed record AcpJsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

public sealed class AcpInboundEnvelope
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = AcpConstants.JsonRpcVersion;

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public AcpJsonRpcError? Error { get; set; }
}

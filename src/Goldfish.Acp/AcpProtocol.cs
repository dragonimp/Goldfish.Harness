using System.Text.Json;
using Goldfish.Acp.Protocol.V1;

namespace Goldfish.Acp;

public static class AcpProtocol
{
    public static AcpJsonRpcRequest<InitializeRequest> Initialize(
        object id,
        string clientName,
        string clientVersion,
        object? metadata = null)
        => new(id, "initialize", new InitializeRequest
        {
            ProtocolVersion = AcpConstants.ProtocolVersion,
            ClientCapabilities = Convert<ClientCapabilities>(new
            {
                fs = new { readTextFile = false, writeTextFile = false },
                terminal = false,
                _meta = new { agentfree = new { transport = "http-sse", version = 1 } }
            }),
            ClientInfo = Convert<ClientInfo>(new { name = clientName, version = clientVersion }),
            _meta = metadata
        });

    public static AcpJsonRpcRequest<NewSessionRequest> NewSession(
        object id,
        string requestedSessionId,
        string cwd,
        object? agentFreeMetadata = null)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd))
            throw new ArgumentException("ACP session/new requires an absolute cwd.", nameof(cwd));

        return new AcpJsonRpcRequest<NewSessionRequest>(id, "session/new", new NewSessionRequest
        {
            Cwd = cwd,
            AdditionalDirectories = [],
            McpServers = [],
            _meta = new
            {
                agentfree = MergeMetadata(agentFreeMetadata, new Dictionary<string, object?>
                {
                    ["requestedSessionId"] = requestedSessionId
                })
            }
        });
    }

    public static AcpJsonRpcRequest<PromptRequest> Prompt(
        object id,
        string sessionId,
        IEnumerable<object> content,
        object? agentFreeMetadata = null)
        => new(id, "session/prompt", new PromptRequest
        {
            SessionId = sessionId,
            Prompt = content.ToList(),
            _meta = new { agentfree = agentFreeMetadata }
        });

    public static AcpJsonRpcNotification<CancelNotification> Cancel(
        string sessionId,
        object? agentFreeMetadata = null)
        => new("session/cancel", new CancelNotification
        {
            SessionId = sessionId,
            _meta = new { agentfree = agentFreeMetadata }
        });

    public static AcpJsonRpcNotification<SessionNotification> SessionUpdate(
        string sessionId,
        object update,
        object? agentFreeMetadata = null)
        => new(AcpConstants.SessionUpdateMethod, new SessionNotification
        {
            SessionId = sessionId,
            Update = update,
            _meta = agentFreeMetadata == null ? null : new { agentfree = agentFreeMetadata }
        });

    public static AcpJsonRpcResponse<PromptResponse> PromptResult(
        object? id,
        string stopReason,
        object? agentFreeMetadata = null)
        => new(id, new PromptResponse { StopReason = stopReason, _meta = Meta(agentFreeMetadata) });

    public static AcpJsonRpcErrorResponse InternalError(object? id, string message, object? data = null)
        => new(id, new AcpJsonRpcError(-32603, message, data == null ? null : JsonSerializer.SerializeToElement(data, AcpJson.Options)));

    public static T Convert<T>(object value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, AcpJson.Options), AcpJson.Options)
            ?? throw new InvalidOperationException($"Unable to convert ACP value to {typeof(T).Name}.");

    private static object? Meta(object? agentFreeMetadata)
        => agentFreeMetadata == null ? null : new { agentfree = agentFreeMetadata };

    private static Dictionary<string, object?> MergeMetadata(object? source, IReadOnlyDictionary<string, object?> additions)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source != null)
        {
            var element = JsonSerializer.SerializeToElement(source, AcpJson.Options);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject()) result[property.Name] = property.Value.Clone();
            }
        }
        foreach (var pair in additions) result[pair.Key] = pair.Value;
        return result;
    }
}

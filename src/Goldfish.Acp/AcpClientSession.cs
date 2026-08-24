using System.Runtime.CompilerServices;
using System.Text.Json;
using Goldfish.Acp.Protocol.V1;

namespace Goldfish.Acp;

public interface IAcpClientTransport
{
    ValueTask<AcpInboundEnvelope> RequestAsync(object request, CancellationToken ct);
    IAsyncEnumerable<AcpInboundEnvelope> StreamAsync(object request, CancellationToken ct);
    ValueTask NotifyAsync(object notification, CancellationToken ct);
}

public sealed class AcpClientSession(
    IAcpClientTransport transport,
    string clientName,
    string clientVersion)
{
    private long _requestId;

    public async ValueTask<InitializeResponse> InitializeAsync(CancellationToken ct)
        => ReadResult<InitializeResponse>(
            await transport.RequestAsync(
                AcpProtocol.Initialize(NextId(), clientName, clientVersion),
                ct));

    public async ValueTask<NewSessionResponse> NewSessionAsync(
        string requestedSessionId,
        string cwd,
        object? agentFreeMetadata,
        CancellationToken ct)
        => ReadResult<NewSessionResponse>(
            await transport.RequestAsync(
                AcpProtocol.NewSession(NextId(), requestedSessionId, cwd, agentFreeMetadata),
                ct));

    public async IAsyncEnumerable<AcpInboundEnvelope> PromptAsync(
        string sessionId,
        IEnumerable<object> content,
        object? agentFreeMetadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var terminalCount = 0;
        await foreach (var envelope in transport.StreamAsync(
                           AcpProtocol.Prompt(NextId(), sessionId, content, agentFreeMetadata),
                           ct))
        {
            var terminal = envelope.Result.HasValue || envelope.Error != null;
            if (terminal && ++terminalCount > 1)
                throw new InvalidDataException("ACP prompt stream returned more than one terminal frame.");
            if (terminalCount > 0 && !terminal)
                throw new InvalidDataException("ACP prompt stream returned data after its terminal frame.");
            yield return envelope;
        }

        if (terminalCount == 0)
            throw new EndOfStreamException("ACP prompt stream ended without a terminal result or error frame.");
    }

    public ValueTask CancelAsync(string sessionId, object? agentFreeMetadata, CancellationToken ct)
        => transport.NotifyAsync(AcpProtocol.Cancel(sessionId, agentFreeMetadata), ct);

    public static TResult ReadResult<TResult>(AcpInboundEnvelope envelope)
    {
        if (!string.Equals(envelope.JsonRpc, AcpConstants.JsonRpcVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported JSON-RPC version: {envelope.JsonRpc}");
        if (envelope.Error != null)
            throw new AcpClientException(envelope.Error.Code, envelope.Error.Message, envelope.Error.Data);
        if (envelope.Result is not { } result)
            throw new InvalidDataException("ACP response does not contain a result.");
        return result.Deserialize<TResult>(AcpJson.Options)
            ?? throw new InvalidDataException($"ACP result cannot be deserialized as {typeof(TResult).Name}.");
    }

    private string NextId() => Interlocked.Increment(ref _requestId).ToString();
}

public sealed class AcpClientException(int code, string message, JsonElement? data)
    : InvalidOperationException(message)
{
    public int Code { get; } = code;
    public JsonElement? RemoteData { get; } = data;
}

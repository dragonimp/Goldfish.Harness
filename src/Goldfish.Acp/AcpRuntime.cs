using Goldfish.Acp.Protocol.V1;

namespace Goldfish.Acp;

public interface IAcpAgent
{
    ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken ct);
    ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken ct);
    ValueTask<PromptResponse> PromptAsync(PromptRequest request, IAcpSessionContext context, CancellationToken ct);
    ValueTask CancelAsync(CancelNotification notification, CancellationToken ct);
}

public interface IAcpSessionContext
{
    string SessionId { get; }
    ValueTask UpdateAsync(SessionUpdate update, CancellationToken ct);
    ValueTask<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken ct);
}

public abstract class AcpAgentBase : IAcpAgent
{
    public abstract ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken ct);
    public abstract ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken ct);
    public abstract ValueTask<PromptResponse> PromptAsync(PromptRequest request, IAcpSessionContext context, CancellationToken ct);
    public abstract ValueTask CancelAsync(CancelNotification notification, CancellationToken ct);
}

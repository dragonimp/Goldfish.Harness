using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Goldfish.Harness;

public interface IGoldfishRunLifecycle
{
    void StartRun(string sessionId, string? agentName);
    void FinishRun(string sessionId);
}

public sealed record GoldfishHarnessSessionRequest(
    AgentInfo AgentInfo,
    string SessionId,
    string Prompt,
    MemoryPartition MemoryPartition,
    bool DisableConfigCache = true,
    UserProfileScope UserProfileScope = UserProfileScope.Global,
    int MaxOutputTokens = 2048,
    float Temperature = 0.2f,
    SkillOptions? SkillOptions = null,
    ISkillSessionStore? SkillSessionStore = null,
    IToolExecutionStore? ToolExecutionStore = null,
    IToolAuthorizationHook? ToolAuthorizationHook = null,
    ReasoningOptions? ReasoningOptions = null);

public sealed class GoldfishHarnessSessionExecutor
{
    private readonly GoldfishHarnessKernel _kernel;
    private readonly GoldfishSessionHistoryStore _historyStore;
    private readonly IMemoryManager _memoryManager;
    private readonly MemoryOptions _memoryOptions;
    private readonly GoldfishHarnessContextAssembler _contextAssembler;
    private readonly GoldfishSessionQueue _sessionQueue;
    private readonly IGoldfishRunLifecycle? _lifecycle;
    private readonly ILogger<GoldfishHarnessSessionExecutor> _logger;

    public GoldfishHarnessSessionExecutor(
        GoldfishHarnessRunner runner,
        GoldfishSessionHistoryStore historyStore,
        IMemoryManager memoryManager,
        MemoryOptions memoryOptions,
        GoldfishSessionQueue sessionQueue,
        IGoldfishSteerSource? steerSource = null,
        IGoldfishRunLifecycle? lifecycle = null,
        ILogger<GoldfishHarnessSessionExecutor>? logger = null,
        IHarnessTurnEventStore? turnEventStore = null)
    {
        _kernel = new GoldfishHarnessKernel(runner, turnEventStore);
        _historyStore = historyStore;
        _memoryManager = memoryManager;
        _memoryOptions = memoryOptions;
        _contextAssembler = new GoldfishHarnessContextAssembler(historyStore, memoryManager, memoryOptions, steerSource);
        _sessionQueue = sessionQueue;
        _lifecycle = lifecycle;
        _logger = logger ?? NullLogger<GoldfishHarnessSessionExecutor>.Instance;
    }

    public async Task<GoldfishHarnessRunResult> RunAsync(
        GoldfishHarnessSessionRequest request,
        CancellationToken ct = default)
    {
        var harnessRequest = await BuildRequestAsync(request, ct);
        _logger.LogInformation(
            "Executing Goldfish Harness agent={AgentName}, historyMessages={Count}",
            request.AgentInfo.Name,
            harnessRequest.History.Count);

        return await _sessionQueue.EnqueueRunAsync(
            harnessRequest,
            async (queuedRequest, queuedCt) =>
            {
                _lifecycle?.StartRun(request.SessionId, request.AgentInfo.Name);
                try
                {
                    var result = await _kernel.RunAsync(queuedRequest, queuedCt);
                    foreach (var ev in result.Events)
                    {
                        UpdateReasoningSelectionCache(request, ev);
                    }
                    return result;
                }
                finally
                {
                    _lifecycle?.FinishRun(request.SessionId);
                }
            },
            ct: ct);
    }

    public async IAsyncEnumerable<GoldfishHarnessEvent> StreamAsync(
        GoldfishHarnessSessionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var harnessRequest = await BuildRequestAsync(request, ct);
        _logger.LogInformation(
            "Streaming Goldfish Harness agent={AgentName}, historyMessages={Count}",
            request.AgentInfo.Name,
            harnessRequest.History.Count);

        async IAsyncEnumerable<GoldfishHarnessEvent> ExecuteQueued(
            GoldfishHarnessRequest queuedRequest,
            [EnumeratorCancellation] CancellationToken queuedCt)
        {
            _lifecycle?.StartRun(request.SessionId, request.AgentInfo.Name);
            try
            {
                await foreach (var ev in _kernel.StreamAsync(queuedRequest, queuedCt).WithCancellation(queuedCt))
                {
                    UpdateReasoningSelectionCache(request, ev);
                    yield return ev;
                }
            }
            finally
            {
                _lifecycle?.FinishRun(request.SessionId);
            }
        }

        var submission = _sessionQueue.Enqueue(harnessRequest, ExecuteQueued);
        try
        {
            await foreach (var ev in submission.Events.WithCancellation(ct))
            {
                yield return ev;
            }
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                _sessionQueue.Cancel(request.SessionId, submission.MessageId);
            }
        }
    }

    public async Task PersistTurnAsync(
        GoldfishHarnessSessionRequest request,
        string answer,
        CancellationToken ct = default)
    {
        await _historyStore.AppendTurnAsync(request.SessionId, request.Prompt, answer);
        try
        {
            await _memoryManager.AddMessageAsync(request.MemoryPartition, new ChatMessage
            {
                Role = "user",
                Content = request.Prompt
            });
            await _memoryManager.AddMessageAsync(request.MemoryPartition, new ChatMessage
            {
                Role = "assistant",
                Content = answer
            });
            await UserProfileMemory.ExtractAndStoreAsync(
                _memoryManager,
                request.MemoryPartition,
                request.Prompt,
            _contextAssembler.CreateMemoryOptions(request.UserProfileScope).UserProfile);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Persist Goldfish memory failed. tenant={TenantId}, user={UserId}, agent={AgentId}, workspace={WorkspaceId}, session={SessionId}",
                request.MemoryPartition.TenantId,
                request.MemoryPartition.UserId,
                request.MemoryPartition.AgentId,
                request.MemoryPartition.WorkspaceId,
                request.MemoryPartition.SessionId);
        }
    }

    private async Task<GoldfishHarnessRequest> BuildRequestAsync(
        GoldfishHarnessSessionRequest request,
        CancellationToken ct)
        => await _contextAssembler.AssembleAsync(request, ct);

    private void UpdateReasoningSelectionCache(
        GoldfishHarnessSessionRequest request,
        GoldfishHarnessEvent ev)
    {
        var options = request.ReasoningOptions ?? ReasoningOptions.Default;
        if (ev.Kind != GoldfishEventKind.ReasoningStrategySelected
            || ev.ReasoningSelection is not { Requested: ReasoningStrategyKind.Auto } selection
            || !options.CacheAutoStrategyInSession)
        {
            return;
        }

        _historyStore.SetReasoningSelection(request.SessionId, selection);
    }

}

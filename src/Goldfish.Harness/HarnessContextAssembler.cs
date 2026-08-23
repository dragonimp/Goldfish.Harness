using Microsoft.Extensions.AI;

namespace Goldfish.Harness;

/// <summary>
/// The sole boundary that assembles persisted conversation and memory into a
/// model request.  Planning and ACP projection must not reach into storage.
/// </summary>
public sealed class GoldfishHarnessContextAssembler
{
    private readonly GoldfishSessionHistoryStore _historyStore;
    private readonly IMemoryManager _memoryManager;
    private readonly MemoryOptions _memoryOptions;
    private readonly IGoldfishSteerSource? _steerSource;

    public GoldfishHarnessContextAssembler(
        GoldfishSessionHistoryStore historyStore,
        IMemoryManager memoryManager,
        MemoryOptions memoryOptions,
        IGoldfishSteerSource? steerSource = null)
    {
        _historyStore = historyStore;
        _memoryManager = memoryManager;
        _memoryOptions = memoryOptions;
        _steerSource = steerSource;
    }

    public async Task<GoldfishHarnessRequest> AssembleAsync(GoldfishHarnessSessionRequest request, CancellationToken ct = default)
    {
        var history = await _historyStore.LoadAsync(request.SessionId);
        var options = CreateMemoryOptions(request.UserProfileScope);
        var memory = await _memoryManager.BuildContextAsync(request.MemoryPartition, request.Prompt, options);
        if (memory.ShortTermMessages.Count == 0 && history.Count > 0)
        {
            foreach (var message in history)
            {
                ct.ThrowIfCancellationRequested();
                await _memoryManager.AddMessageAsync(request.MemoryPartition, new ChatMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    ToolCallId = message.ToolCallId,
                    CreatedAt = message.CreatedAt
                });
            }
            memory = await _memoryManager.BuildContextAsync(request.MemoryPartition, request.Prompt, options);
        }

        return new GoldfishHarnessRequest(
            request.AgentInfo,
            request.SessionId,
            request.Prompt,
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, request.Prompt),
            history,
            request.MaxOutputTokens,
            request.Temperature,
            request.DisableConfigCache,
            MemoryOptions: _memoryOptions,
            MemoryContext: memory,
            SteerSource: _steerSource,
            SkillOptions: request.SkillOptions,
            SkillSessionStore: request.SkillSessionStore,
            ToolExecutionStore: request.ToolExecutionStore,
            ToolAuthorizationHook: request.ToolAuthorizationHook,
            ReasoningOptions: request.ReasoningOptions,
            CachedReasoningSelection: _historyStore.GetReasoningSelection(request.SessionId));
    }

    public MemoryOptions CreateMemoryOptions(UserProfileScope scope)
        => new()
        {
            ShortTerm = _memoryOptions.ShortTerm,
            MediumTerm = _memoryOptions.MediumTerm,
            LongTerm = _memoryOptions.LongTerm,
            Embedding = _memoryOptions.Embedding,
            Sqlite = _memoryOptions.Sqlite,
            Admission = _memoryOptions.Admission,
            UserProfile = new UserProfileMemoryOptions
            {
                Enabled = _memoryOptions.UserProfile.Enabled,
                AutoExtract = _memoryOptions.UserProfile.AutoExtract,
                MaxMemories = _memoryOptions.UserProfile.MaxMemories,
                MinimumConfidence = _memoryOptions.UserProfile.MinimumConfidence,
                Scope = scope
            }
        };
}

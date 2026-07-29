using System.Collections.Concurrent;
using System.Text.Json;

namespace Goldfish.Harness;

public sealed class GoldfishSessionHistoryStore
{
    private const int MaxMessagesPerSession = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _sessionsDir;
    private readonly ConcurrentDictionary<string, ReasoningStrategySelection> _reasoningSelections = new(StringComparer.Ordinal);

    public GoldfishSessionHistoryStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".goldfish",
            "sessions"))
    {
    }

    public GoldfishSessionHistoryStore(string sessionsDir)
    {
        _sessionsDir = sessionsDir;
        Directory.CreateDirectory(_sessionsDir);
    }

    public async Task<List<ChatMessage>> LoadAsync(string sessionId)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task AppendTurnAsync(string sessionId, string userText, string assistantText)
    {
        var history = await LoadAsync(sessionId);
        history.Add(new ChatMessage { Role = "user", Content = userText });
        history.Add(new ChatMessage { Role = "assistant", Content = assistantText });
        if (history.Count > MaxMessagesPerSession)
        {
            history = history.Skip(history.Count - MaxMessagesPerSession).ToList();
        }

        await File.WriteAllTextAsync(GetPath(sessionId), JsonSerializer.Serialize(history, JsonOptions));
    }

    public ReasoningStrategySelection? GetReasoningSelection(string sessionId)
        => _reasoningSelections.TryGetValue(NormalizeSessionId(sessionId), out var selection)
            ? selection
            : null;

    public void SetReasoningSelection(string sessionId, ReasoningStrategySelection selection)
        => _reasoningSelections[NormalizeSessionId(sessionId)] = selection;

    public void ClearReasoningSelection(string sessionId)
        => _reasoningSelections.TryRemove(NormalizeSessionId(sessionId), out _);

    private string GetPath(string sessionId)
    {
        var safe = NormalizeSessionId(sessionId);
        return Path.Combine(_sessionsDir, $"{safe}.json");
    }

    private static string NormalizeSessionId(string sessionId)
    {
        var safe = string.Concat((sessionId ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrEmpty(safe)) safe = "default";
        return safe;
    }
}

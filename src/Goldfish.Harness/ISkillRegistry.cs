using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Goldfish.Harness;

public sealed class SkillOptions
{
    public bool Enabled { get; set; } = true;
    public bool ExposeSkillIndexTool { get; set; } = true;
    public bool ExposeLoadSkillTool { get; set; } = true;
    public bool PersistLoadedSkills { get; set; } = true;
    public bool RestrictToolsToLoadedSkills { get; set; } = false;
    public int MaxSearchResults { get; set; } = 10;
    public int MaxLoadedSkills { get; set; } = 5;
    public ISet<string> AllowedSkills { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static SkillOptions Default => new();
}

public sealed record SkillMetadata
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Version { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public string? SourcePath { get; init; }
}

public sealed record SkillContent
{
    public SkillMetadata Metadata { get; init; } = new();
    public string Instructions { get; init; } = string.Empty;
}

public interface ISkillRegistry
{
    IReadOnlyList<SkillMetadata> List();
    IReadOnlyList<SkillMetadata> Search(string query, int limit = 10);
    SkillContent? Load(string name);
}

public sealed record SkillSessionKey
{
    public string TenantId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string AgentId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
}

public sealed record SkillSessionEntry
{
    public string SkillName { get; init; } = string.Empty;
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Source { get; init; }
}

public interface ISkillSessionStore
{
    Task<IReadOnlyList<SkillSessionEntry>> LoadAsync(SkillSessionKey key, CancellationToken ct = default);
    Task RecordLoadedAsync(SkillSessionKey key, SkillSessionEntry entry, CancellationToken ct = default);
}

public sealed class NullSkillSessionStore : ISkillSessionStore
{
    public static NullSkillSessionStore Instance { get; } = new();

    private NullSkillSessionStore()
    {
    }

    public Task<IReadOnlyList<SkillSessionEntry>> LoadAsync(SkillSessionKey key, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SkillSessionEntry>>([]);

    public Task RecordLoadedAsync(SkillSessionKey key, SkillSessionEntry entry, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class InMemorySkillSessionStore : ISkillSessionStore
{
    private readonly Dictionary<string, Dictionary<string, SkillSessionEntry>> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public Task<IReadOnlyList<SkillSessionEntry>> LoadAsync(SkillSessionKey key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(BuildKey(key), out var entries))
            {
                return Task.FromResult<IReadOnlyList<SkillSessionEntry>>([]);
            }

            return Task.FromResult<IReadOnlyList<SkillSessionEntry>>(
                entries.Values.OrderBy(entry => entry.LoadedAt).ToList());
        }
    }

    public Task RecordLoadedAsync(SkillSessionKey key, SkillSessionEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.SkillName))
        {
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            var partitionKey = BuildKey(key);
            if (!_entries.TryGetValue(partitionKey, out var entries))
            {
                entries = new Dictionary<string, SkillSessionEntry>(StringComparer.OrdinalIgnoreCase);
                _entries[partitionKey] = entries;
            }

            entries[entry.SkillName.Trim()] = entry;
        }

        return Task.CompletedTask;
    }

    private static string BuildKey(SkillSessionKey key)
        => string.Join('\u001f', key.TenantId, key.UserId, key.AgentId, key.WorkspaceId, key.SessionId);
}

public sealed class InMemorySkillRegistry : ISkillRegistry
{
    private readonly Dictionary<string, SkillContent> _skills;

    public InMemorySkillRegistry(IEnumerable<SkillContent> skills)
    {
        _skills = skills.ToDictionary(s => s.Metadata.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SkillMetadata> List()
        => _skills.Values.Select(s => s.Metadata).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<SkillMetadata> Search(string query, int limit = 10)
    {
        var normalizedLimit = Math.Max(0, limit);
        if (normalizedLimit == 0) return [];
        if (string.IsNullOrWhiteSpace(query)) return List().Take(normalizedLimit).ToList();

        var terms = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return _skills.Values
            .Select(skill => new
            {
                skill.Metadata,
                Score = Score(skill.Metadata, terms)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedLimit)
            .Select(item => item.Metadata)
            .ToList();
    }

    public SkillContent? Load(string name)
        => _skills.TryGetValue(name, out var skill) ? skill : null;

    private static int Score(SkillMetadata metadata, IReadOnlyList<string> terms)
    {
        var haystack = $"{metadata.Name} {metadata.Description} {string.Join(' ', metadata.AllowedTools)}";
        var score = 0;
        foreach (var term in terms)
        {
            if (metadata.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 5;
            if (metadata.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) score++;
        }
        return score;
    }
}

public sealed class FileSystemSkillRegistry : ISkillRegistry
{
    private readonly string _rootDirectory;
    private readonly Lazy<InMemorySkillRegistry> _inner;

    public FileSystemSkillRegistry(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
        _inner = new Lazy<InMemorySkillRegistry>(() => new InMemorySkillRegistry(LoadSkills(_rootDirectory)));
    }

    public IReadOnlyList<SkillMetadata> List() => _inner.Value.List();

    public IReadOnlyList<SkillMetadata> Search(string query, int limit = 10) => _inner.Value.Search(query, limit);

    public SkillContent? Load(string name) => _inner.Value.Load(name);

    private static IEnumerable<SkillContent> LoadSkills(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(rootDirectory, "SKILL.md", SearchOption.AllDirectories)
            .Select(ParseSkillFile)
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Metadata.Name))
            .ToList();
    }

    private static SkillContent ParseSkillFile(string path)
    {
        var text = File.ReadAllText(path);
        var (frontMatter, body) = SplitFrontMatter(text);
        var values = ParseFrontMatter(frontMatter);
        var directoryName = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        var name = GetValue(values, "name") ?? directoryName;
        var description = GetValue(values, "description") ?? ExtractDescription(body);
        var version = GetValue(values, "version");
        var allowedTools = GetList(values, "allowed-tools")
            .Concat(GetList(values, "tools"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SkillContent
        {
            Metadata = new SkillMetadata
            {
                Name = name.Trim(),
                Description = description.Trim(),
                Version = version?.Trim(),
                AllowedTools = allowedTools,
                SourcePath = path
            },
            Instructions = body.Trim()
        };
    }

    private static (string FrontMatter, string Body) SplitFrontMatter(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal)) return (string.Empty, text);
        var match = Regex.Match(text, @"\A---\s*\r?\n(?<front>.*?)\r?\n---\s*\r?\n(?<body>.*)\z", RegexOptions.Singleline);
        return match.Success
            ? (match.Groups["front"].Value, match.Groups["body"].Value)
            : (string.Empty, text);
    }

    private static Dictionary<string, List<string>> ParseFrontMatter(string frontMatter)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        foreach (var rawLine in frontMatter.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var itemMatch = Regex.Match(line, @"^\s*-\s*(?<value>.+)$");
            if (itemMatch.Success && currentKey != null)
            {
                result[currentKey].Add(Unquote(itemMatch.Groups["value"].Value.Trim()));
                continue;
            }

            var kvMatch = Regex.Match(line, @"^(?<key>[A-Za-z0-9_.-]+)\s*:\s*(?<value>.*)$");
            if (!kvMatch.Success) continue;

            currentKey = kvMatch.Groups["key"].Value.Trim();
            var value = kvMatch.Groups["value"].Value.Trim();
            result[currentKey] = [];
            if (!string.IsNullOrWhiteSpace(value) && value != ">" && value != "|")
            {
                result[currentKey].Add(Unquote(value));
            }
        }
        return result;
    }

    private static string? GetValue(IReadOnlyDictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var items) && items.Count > 0
            ? string.Join(Environment.NewLine, items)
            : null;

    private static IReadOnlyList<string> GetList(IReadOnlyDictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var items)
            ? items.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList()
            : [];

    private static string ExtractDescription(string body)
    {
        foreach (var line in body.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)) return trimmed;
        }
        return string.Empty;
    }

    private static string Unquote(string value)
        => value.Trim().Trim('"', '\'');
}

internal sealed class SkillRuntimeState
{
    private readonly SkillOptions _options;
    private readonly Dictionary<string, SkillContent> _loadedSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _restoredMissingSkills = [];

    public SkillRuntimeState(SkillOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<SkillContent> LoadedSkills => _loadedSkills.Values.ToList();
    public IReadOnlyList<string> RestoredMissingSkills => _restoredMissingSkills;

    public bool CanLoad(string skillName)
    {
        if (_loadedSkills.ContainsKey(skillName)) return true;
        if (_loadedSkills.Count >= Math.Max(0, _options.MaxLoadedSkills)) return false;
        return true;
    }

    public bool IsLoaded(string skillName) => _loadedSkills.ContainsKey(skillName);

    public void MarkLoaded(SkillContent skill)
    {
        _loadedSkills[skill.Metadata.Name] = skill;
    }

    public void MarkRestoredMissing(string skillName)
    {
        if (!_restoredMissingSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase))
        {
            _restoredMissingSkills.Add(skillName);
        }
    }

    public ISet<string> GetAllowedToolIds()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in _loadedSkills.Values)
        {
            foreach (var tool in skill.Metadata.AllowedTools)
            {
                allowed.Add(tool);
            }
        }
        return allowed;
    }

    public string BuildLoadedSkillsPrompt()
    {
        if (_loadedSkills.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## 已加载 Skills");
        sb.AppendLine("以下 Skill 指令已注入当前运行上下文。执行相关任务时优先遵守这些流程、约束和工具边界。");
        foreach (var skill in _loadedSkills.Values)
        {
            sb.AppendLine();
            sb.AppendLine($"### {skill.Metadata.Name}");
            if (!string.IsNullOrWhiteSpace(skill.Metadata.Description))
            {
                sb.AppendLine(skill.Metadata.Description);
                sb.AppendLine();
            }
            if (skill.Metadata.AllowedTools.Count > 0)
            {
                sb.AppendLine("Allowed tools:");
                foreach (var tool in skill.Metadata.AllowedTools)
                {
                    sb.AppendLine($"- {tool}");
                }
                sb.AppendLine();
            }
            sb.AppendLine(skill.Instructions);
        }
        return sb.ToString().TrimEnd();
    }
}

internal sealed class SkillIndexTool : ITool
{
    private readonly ISkillRegistry _registry;
    private readonly SkillOptions _options;

    public SkillIndexTool(ISkillRegistry registry, SkillOptions options)
    {
        _registry = registry;
        _options = options;
    }

    public string Id => "goldfish.skill_index";
    public string Name => "goldfish_skill_index";
    public string Description => "Internal Goldfish capability: search available skills by task description before choosing a skill to load.";
    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Task description or keywords used to search skills." },
            "limit": { "type": "integer", "description": "Maximum number of skill candidates to return." }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    public Task<bool> IsAvailableAsync() => Task.FromResult(_options.Enabled && _options.ExposeSkillIndexTool);

    public Task<ToolResult> ExecuteAsync(string arguments)
    {
        var input = ParseArguments(arguments);
        var limit = Math.Clamp(input.Limit ?? _options.MaxSearchResults, 1, Math.Max(1, _options.MaxSearchResults));
        var skills = _registry.Search(input.Query ?? string.Empty, limit)
            .Where(IsAllowed)
            .Select(skill => new SkillIndexResult(
                skill.Name,
                skill.Description,
                skill.Version,
                skill.AllowedTools))
            .ToList();

        return Task.FromResult(new ToolResult
        {
            Success = true,
            Data = new
            {
                skills,
                count = skills.Count,
                instruction = "Call goldfish_load_skill with the exact skill name when one of these skills is useful."
            },
            DisplayText = BuildDisplayText(skills)
        });
    }

    private bool IsAllowed(SkillMetadata skill)
        => _options.AllowedSkills.Count == 0 || _options.AllowedSkills.Contains(skill.Name);

    private static string BuildDisplayText(IReadOnlyList<SkillIndexResult> skills)
    {
        if (skills.Count == 0)
        {
            return "找到 0 个可用 Skill。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"找到 {skills.Count} 个可用 Skill。调用 goldfish_load_skill 时必须使用下面的 exact skill_name。");
        foreach (var skill in skills)
        {
            sb.AppendLine($"- skill_name: {skill.Name}");
            if (!string.IsNullOrWhiteSpace(skill.Description))
            {
                sb.AppendLine($"  description: {skill.Description}");
            }
            if (skill.AllowedTools is { Count: > 0 })
            {
                sb.AppendLine($"  allowed_tools: {string.Join(", ", skill.AllowedTools)}");
            }
        }
        return sb.ToString().Trim();
    }

    private static SkillIndexArguments ParseArguments(string arguments)
    {
        try
        {
            return JsonSerializer.Deserialize<SkillIndexArguments>(arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private sealed record SkillIndexArguments
    {
        public string? Query { get; init; }
        public int? Limit { get; init; }
    }

    private sealed record SkillIndexResult(
        string Name,
        string Description,
        string? Version,
        IReadOnlyList<string> AllowedTools);
}

internal sealed class LoadSkillTool : ITool
{
    private readonly ISkillRegistry _registry;
    private readonly SkillRuntimeState _state;
    private readonly SkillOptions _options;
    private readonly ISkillSessionStore _sessionStore;
    private readonly SkillSessionKey _sessionKey;

    public LoadSkillTool(
        ISkillRegistry registry,
        SkillRuntimeState state,
        SkillOptions options,
        ISkillSessionStore? sessionStore = null,
        SkillSessionKey? sessionKey = null)
    {
        _registry = registry;
        _state = state;
        _options = options;
        _sessionStore = sessionStore ?? NullSkillSessionStore.Instance;
        _sessionKey = sessionKey ?? new SkillSessionKey();
    }

    public string Id => "goldfish.load_skill";
    public string Name => "goldfish_load_skill";
    public string Description => "Internal Goldfish capability: load a selected skill's instructions into the current agent context.";
    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "skill_name": { "type": "string", "description": "Exact skill name from goldfish_skill_index results." }
          },
          "required": ["skill_name"],
          "additionalProperties": false
        }
        """;

    public Task<bool> IsAvailableAsync() => Task.FromResult(_options.Enabled && _options.ExposeLoadSkillTool);

    public async Task<ToolResult> ExecuteAsync(string arguments)
    {
        var input = ParseArguments(arguments);
        if (string.IsNullOrWhiteSpace(input.SkillName))
        {
            return new ToolResult { Success = false, Error = "skill_name is required." };
        }

        var skillName = input.SkillName.Trim();
        if (_options.AllowedSkills.Count > 0 && !_options.AllowedSkills.Contains(skillName))
        {
            return new ToolResult { Success = false, Error = $"Skill is not allowed for this run: {skillName}" };
        }

        if (!_state.CanLoad(skillName))
        {
            return new ToolResult { Success = false, Error = $"Maximum loaded skills reached: {_options.MaxLoadedSkills}" };
        }

        var skill = _registry.Load(skillName);
        if (skill == null)
        {
            return new ToolResult { Success = false, Error = $"Skill not found: {skillName}" };
        }

        var alreadyLoaded = _state.IsLoaded(skill.Metadata.Name);
        _state.MarkLoaded(skill);
        if (_options.PersistLoadedSkills)
        {
            await _sessionStore.RecordLoadedAsync(_sessionKey, new SkillSessionEntry
            {
                SkillName = skill.Metadata.Name,
                LoadedAt = DateTimeOffset.UtcNow,
                Source = "goldfish_load_skill"
            });
        }

        return new ToolResult
        {
            Success = true,
            Data = new
            {
                status = "loaded",
                skill = skill.Metadata.Name,
                skill.Metadata.Description,
                skill.Metadata.AllowedTools,
                instructions = skill.Instructions
            },
            DisplayText = alreadyLoaded
                ? $"Skill 已存在: {skill.Metadata.Name}"
                : $"已加载 Skill: {skill.Metadata.Name}"
        };
    }

    private static LoadSkillArguments ParseArguments(string arguments)
    {
        try
        {
            return JsonSerializer.Deserialize<LoadSkillArguments>(arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private sealed record LoadSkillArguments
    {
        [JsonPropertyName("skill_name")]
        public string? SkillName { get; init; }
    }
}

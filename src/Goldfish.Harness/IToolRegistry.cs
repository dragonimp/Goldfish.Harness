using System.Reflection;

namespace Goldfish.Harness;

/// <summary>
/// 工具执行结果
/// </summary>
public class ToolResult
{
    public bool Success { get; set; }

    /// <summary>给模型做严肃推理的结构化结果。</summary>
    public object? Data { get; set; }

    public string? Error { get; set; }

    /// <summary>给模型和 UI 的可读展示文本。工具自带时优先于对 Data 的反向格式化。</summary>
    public string? DisplayText { get; set; }

    /// <summary>给网关文件发送链路的附件（如 [GatewayFile] 标记或附件对象）。</summary>
    public IReadOnlyList<object>? Attachments { get; set; }

    /// <summary>给 trace/调试的元信息（耗时等）。</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; set; }
}

/// <summary>
/// 工具接口
/// </summary>
public interface ITool
{
    /// <summary>
    /// 工具唯一标识
    /// </summary>
    string Id { get; }

    /// <summary>
    /// 工具名称（用于 LLM 调用）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 工具描述（用于 LLM 理解）
    /// </summary>
    string Description { get; }

    /// <summary>
    /// 工具参数 Schema（JSON Schema 格式）
    /// </summary>
    string ParametersSchema { get; }

    /// <summary>
    /// 执行工具
    /// </summary>
    Task<ToolResult> ExecuteAsync(string arguments);

    /// <summary>
    /// 工具是否可用（依赖检查）
    /// </summary>
    Task<bool> IsAvailableAsync();
}

/// <summary>
/// 工具注册表 — 负责工具的发现、注册和执行
/// 参考 AIFunctionFactory 的设计
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// 注册一个工具
    /// </summary>
    void Register(ITool tool);

    /// <summary>
    /// 注册一组工具
    /// </summary>
    void RegisterRange(IEnumerable<ITool> tools);

    /// <summary>
    /// 从程序集自动发现并注册工具
    /// </summary>
    void DiscoverFromAssembly(Assembly assembly);

    /// <summary>
    /// 获取已注册的工具列表
    /// </summary>
    IList<ITool> GetAll();

    /// <summary>
    /// 根据 ID 获取工具
    /// </summary>
    ITool? GetById(string toolId);

    /// <summary>
    /// 执行工具调用
    /// </summary>
    Task<ToolResult> ExecuteAsync(string toolId, string arguments);
}

/// <summary>
/// 工具注册表实现
/// </summary>
public class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly object _lock = new();

    public void Register(ITool tool)
    {
        if (string.IsNullOrEmpty(tool.Id))
            throw new ArgumentException("Tool Id cannot be null or empty.", nameof(tool));

        lock (_lock)
        {
            _tools[tool.Id] = tool;
        }
    }

    public void RegisterRange(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            Register(tool);
        }
    }

    public void DiscoverFromAssembly(Assembly assembly)
    {
        var toolTypes = assembly.GetTypes()
            .Where(t => typeof(ITool).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        foreach (var toolType in toolTypes)
        {
            var tool = (ITool)Activator.CreateInstance(toolType)!;
            Register(tool);
        }
    }

    public IList<ITool> GetAll()
    {
        lock (_lock)
        {
            return _tools.Values.ToList();
        }
    }

    public ITool? GetById(string toolId)
    {
        lock (_lock)
        {
            _tools.TryGetValue(toolId, out var tool);
            return tool;
        }
    }

    public async Task<ToolResult> ExecuteAsync(string toolId, string arguments)
    {
        var tool = GetById(toolId);
        if (tool == null)
            return new ToolResult { Success = false, Error = $"Tool not found: {toolId}" };

        var available = await tool.IsAvailableAsync();
        if (!available)
            return new ToolResult { Success = false, Error = $"Tool '{tool.Name}' is not available" };

        try
        {
            var result = await tool.ExecuteAsync(arguments);
            return result;
        }
        catch (Exception ex)
        {
            return new ToolResult { Success = false, Error = ex.Message };
        }
    }
}

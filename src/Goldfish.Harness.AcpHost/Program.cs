using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Goldfish.Harness;

return await new AcpHost(Console.In, Console.Out, Console.Error).RunAsync();

public static class AcpHostAssembly;

internal sealed class AcpHost(TextReader input, TextWriter output, TextWriter error)
{
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly GoldfishAcpEventProjector _projector = new();

    public async Task<int> RunAsync()
    {
        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                await WriteAsync(GoldfishAcpProtocol.Error(null, -32700, ex.Message));
                continue;
            }

            using (document)
            {
                var request = document.RootElement.Clone();
                var method = ReadString(request, "method");
                var id = request.TryGetProperty("id", out var idElement) ? idElement.Clone() : default(JsonElement?);
                try
                {
                    switch (method)
                    {
                        case "initialize":
                            await WriteAsync(GoldfishAcpProtocol.Response(
                                id,
                                GoldfishAcpProtocol.InitializeResult("Goldfish.Harness", typeof(AcpHost).Assembly.GetName().Version?.ToString() ?? "1.0.0")));
                            break;
                        case "session/new":
                            await CreateSessionAsync(id, request);
                            break;
                        case "session/prompt":
                            StartPrompt(id, request);
                            break;
                        case "session/cancel":
                            await CancelSessionAsync(id, request);
                            break;
                        case "shutdown":
                            foreach (var run in _activeRuns.Values) run.Cancel();
                            await WriteAsync(GoldfishAcpProtocol.Response(id, new { }));
                            return 0;
                        default:
                            await WriteAsync(GoldfishAcpProtocol.Error(id, -32601, $"Unsupported ACP method: {method}"));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    await error.WriteLineAsync(ex.ToString());
                    await WriteAsync(GoldfishAcpProtocol.Error(id, -32603, ex.Message));
                }
            }
        }

        foreach (var run in _activeRuns.Values) run.Cancel();
        return 0;
    }

    private async Task CreateSessionAsync(JsonElement? id, JsonElement request)
    {
        var parameters = RequiredObject(request, "params");
        var cwd = ReadString(parameters, "cwd");
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd))
            throw new InvalidOperationException("session/new requires an absolute cwd.");

        var sessionId = ReadRequestedSessionId(parameters) ?? Guid.NewGuid().ToString("N");
        var runtime = ReadRuntime(parameters);
        var session = new HostSession(sessionId, Path.GetFullPath(cwd), runtime);
        if (!_sessions.TryAdd(sessionId, session))
            throw new InvalidOperationException($"ACP session already exists: {sessionId}");

        await WriteAsync(GoldfishAcpProtocol.Response(id, new
        {
            sessionId,
            _meta = new { agentfree = new { runtime = "goldfish-harness", cwd = session.Cwd } }
        }));
    }

    private void StartPrompt(JsonElement? id, JsonElement request)
    {
        var parameters = RequiredObject(request, "params");
        var sessionId = ReadString(parameters, "sessionId")
            ?? throw new InvalidOperationException("session/prompt requires sessionId.");
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException($"ACP session not found: {sessionId}");
        var run = new ActiveRun();
        if (!_activeRuns.TryAdd(sessionId, run))
        {
            run.Dispose();
            throw new InvalidOperationException($"ACP session already has an active run: {sessionId}");
        }

        var prompt = ReadPrompt(parameters);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            var stopReason = "end_turn";
            try
            {
                await ExecutePromptAsync(session, prompt, run.Token);
            }
            catch (OperationCanceledException)
            {
                stopReason = "cancelled";
            }
            catch (Exception ex)
            {
                failure = ex;
                stopReason = "refusal";
            }
            finally
            {
                _activeRuns.TryRemove(sessionId, out _);
                run.Completion.TrySetResult();
                run.Dispose();
            }

            if (failure is not null)
            {
                await error.WriteLineAsync(failure.ToString());
                await WriteAsync(GoldfishAcpProtocol.RuntimeError(session.SessionId, failure.Message, failure.GetType().Name));
            }
            await WriteAsync(GoldfishAcpProtocol.PromptResult(id, stopReason));
        });
    }

    private async Task ExecutePromptAsync(HostSession session, string prompt, CancellationToken ct)
    {
        var runtime = session.Runtime;
        using var chatClient = new WhitespacePreservingOpenAiChatClient(
            runtime.BaseUrl,
            runtime.ApiKey,
            runtime.Model,
            runtime.Headers,
            runtime.MaxOutputTokens);
        var tools = await HostToolRegistry.CreateAsync(session.Cwd, runtime.Context, runtime.McpServers, ct);
        ISkillRegistry? skills = string.IsNullOrWhiteSpace(runtime.SkillsRoot)
            ? null
            : new FileSystemSkillRegistry(runtime.SkillsRoot);
        var runner = new GoldfishHarnessRunner(chatClient, tools, skillRegistry: skills);
        var stateRoot = runtime.StateRoot;
        Directory.CreateDirectory(stateRoot);
        var history = new GoldfishSessionHistoryStore(Path.Combine(stateRoot, "sessions"));
        var memoryOptions = MemoryOptions.Default;
        var executor = new GoldfishHarnessSessionExecutor(
            runner,
            history,
            new InMemoryMemoryManager(),
            memoryOptions,
            new GoldfishSessionQueue());
        var request = new GoldfishHarnessSessionRequest(
            new AgentInfo
            {
                Id = runtime.AgentId,
                Name = runtime.AgentName,
                Description = runtime.Description,
                AgentType = runtime.AgentType,
                SystemPrompt = runtime.SystemPrompt
            },
            session.SessionId,
            prompt,
            runtime.MemoryPartition with { SessionId = session.SessionId },
            MaxOutputTokens: runtime.MaxOutputTokens ?? 2048,
            Temperature: runtime.Temperature);

        var answer = new StringBuilder();
        await foreach (var ev in executor.StreamAsync(request, ct).WithCancellation(ct))
        {
            if (ev.Kind == GoldfishEventKind.TextDelta) answer.Append(ev.Delta);
            foreach (var frame in _projector.Project(session.SessionId, ev))
                await WriteAsync(frame);
        }

        if (answer.Length > 0)
            await executor.PersistTurnAsync(request, answer.ToString(), ct);
    }

    private async Task CancelSessionAsync(JsonElement? id, JsonElement request)
    {
        var parameters = RequiredObject(request, "params");
        var sessionId = ReadString(parameters, "sessionId")
            ?? throw new InvalidOperationException("session/cancel requires sessionId.");
        var cancelled = _activeRuns.TryGetValue(sessionId, out var run);
        if (run is not null)
        {
            run.Cancel();
            await run.Completion.Task;
        }
        await WriteAsync(GoldfishAcpProtocol.Response(id, new { cancelled }));
    }

    private sealed class ActiveRun : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private bool _disposed;

        public CancellationToken Token => _cancellation.Token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            lock (_gate)
            {
                if (!_disposed) _cancellation.Cancel();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }

    private async Task WriteAsync(object frame)
    {
        var json = JsonSerializer.Serialize(frame, GoldfishAcpProtocol.JsonOptions);
        await _writeLock.WaitAsync();
        try
        {
            await output.WriteLineAsync(json);
            await output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static HostRuntime ReadRuntime(JsonElement parameters)
    {
        var meta = parameters.TryGetProperty("_meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object
            ? metaElement
            : default;
        var agentfree = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("agentfree", out var agentfreeElement)
            ? agentfreeElement
            : default;
        var runtime = agentfree.ValueKind == JsonValueKind.Object && agentfree.TryGetProperty("runtime", out var runtimeElement)
            ? runtimeElement
            : default;
        if (runtime.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("session/new requires _meta.agentfree.runtime.");

        var stateRoot = ReadString(runtime, "stateRoot")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".goldfish", "acp-host");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (runtime.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in headersElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    headers[property.Name] = property.Value.GetString()!;
            }
        }

        return new HostRuntime(
            ReadString(runtime, "agentType") ?? "Goldfish",
            ReadString(runtime, "baseUrl") ?? "https://api.openai.com/v1",
            ReadString(runtime, "apiKey") ?? string.Empty,
            ReadString(runtime, "model") ?? "gpt-4o-mini",
            ReadString(runtime, "systemPrompt") ?? string.Empty,
            ReadString(runtime, "agentId") ?? "goldfish",
            ReadString(runtime, "agentName") ?? "Goldfish",
            ReadString(runtime, "description"),
            ReadString(runtime, "skillsRoot"),
            Path.GetFullPath(stateRoot),
            ReadInt(runtime, "maxOutputTokens"),
            ReadFloat(runtime, "temperature") ?? 0.2f,
            headers,
            ReadStringMap(runtime, "context"),
            ReadMcpServers(runtime),
            new MemoryPartition
            {
                TenantId = ReadNestedString(runtime, "memory", "tenantId") ?? string.Empty,
                UserId = ReadNestedString(runtime, "memory", "userId") ?? string.Empty,
                AgentId = ReadNestedString(runtime, "memory", "agentId") ?? string.Empty,
                WorkspaceId = ReadNestedString(runtime, "memory", "workspaceId") ?? string.Empty
            });
    }

    private static string? ReadRequestedSessionId(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("agentfree", out var agentfree) || agentfree.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(agentfree, "requestedSessionId");
    }

    private static string ReadPrompt(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("prompt", out var prompt)) return string.Empty;
        if (prompt.ValueKind == JsonValueKind.String) return prompt.GetString() ?? string.Empty;
        if (prompt.ValueKind != JsonValueKind.Array) return string.Empty;
        var text = new StringBuilder();
        foreach (var block in prompt.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && string.Equals(ReadString(block, "type"), "text", StringComparison.OrdinalIgnoreCase))
                text.Append(ReadString(block, "text"));
        }
        return text.ToString();
    }

    private static JsonElement RequiredObject(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException($"{name} must be an object.");

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed) ? parsed : null;

    private static float? ReadFloat(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetSingle(out var parsed) ? parsed : null;

    private static string? ReadNestedString(JsonElement root, string objectName, string name)
        => root.TryGetProperty(objectName, out var nested) ? ReadString(nested, name) : null;

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in map.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            if (!string.IsNullOrWhiteSpace(value)) result[property.Name] = value;
        }
        return result;
    }

    private static IReadOnlyList<HostMcpServer> ReadMcpServers(JsonElement runtime)
    {
        if (!runtime.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return Array.Empty<HostMcpServer>();
        var result = new List<HostMcpServer>();
        foreach (var server in servers.EnumerateArray())
        {
            var url = ReadString(server, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var tools = server.TryGetProperty("tools", out var toolArray) && toolArray.ValueKind == JsonValueKind.Array
                ? toolArray.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var overrides = ReadStringMap(server, "descriptionOverrides");
            result.Add(new HostMcpServer(url, ReadString(server, "token"), tools, overrides));
        }
        return result;
    }
}

internal sealed record HostSession(string SessionId, string Cwd, HostRuntime Runtime);

internal sealed record HostRuntime(
    string AgentType,
    string BaseUrl,
    string ApiKey,
    string Model,
    string SystemPrompt,
    string AgentId,
    string AgentName,
    string? Description,
    string? SkillsRoot,
    string StateRoot,
    int? MaxOutputTokens,
    float Temperature,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Context,
    IReadOnlyList<HostMcpServer> McpServers,
    MemoryPartition MemoryPartition);

internal sealed record HostMcpServer(
    string Url,
    string? Token,
    IReadOnlySet<string> Tools,
    IReadOnlyDictionary<string, string> DescriptionOverrides);

internal static class HostToolRegistry
{
    public static async Task<IToolRegistry> CreateAsync(
        string workspace,
        IReadOnlyDictionary<string, string> context,
        IReadOnlyList<HostMcpServer> mcpServers,
        CancellationToken ct)
    {
        var registry = new ToolRegistry();
        registry.Register(new DelegateTool("read_file", "read_file", "读取 workspace 内的文本文件。", PathSchema(), async args =>
        {
            var path = ResolvePath(workspace, ReadRequired(args, "path"));
            return new ToolResult { Success = true, Data = await File.ReadAllTextAsync(path), DisplayText = await File.ReadAllTextAsync(path) };
        }));
        registry.Register(new DelegateTool("write_file", "write_file", "写入 workspace 内的文本文件。", WriteSchema(), async args =>
        {
            var path = ResolvePath(workspace, ReadRequired(args, "path"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = ReadRequired(args, "content");
            await File.WriteAllTextAsync(path, content);
            return new ToolResult { Success = true, Data = new { path, bytes = Encoding.UTF8.GetByteCount(content) }, DisplayText = $"已写入 {path}" };
        }));
        registry.Register(new DelegateTool("list_directory", "list_directory", "列出 workspace 内目录。", PathSchema(), args =>
        {
            var path = ResolvePath(workspace, ReadOptional(args, "path") ?? ".");
            var entries = Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Order().ToArray();
            return Task.FromResult(new ToolResult { Success = true, Data = entries, DisplayText = string.Join('\n', entries) });
        }));
        registry.Register(new DelegateTool("execute_command", "execute_command", "在 workspace 内执行命令。", CommandSchema(), async args =>
        {
            var command = ReadRequired(args, "command");
            var timeoutSeconds = Math.Clamp(ReadOptionalInt(args, "timeoutSeconds") ?? 120, 1, 900);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/zsh";
            var shellArgs = OperatingSystem.IsWindows() ? $"/d /s /c \"{command}\"" : $"-lc {Quote(command)}";
            using var process = Process.Start(new ProcessStartInfo(shell, shellArgs)
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Failed to start command.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw new TimeoutException($"Command timed out after {timeoutSeconds} seconds.");
            }
            var output = Limit(await stdout, 64 * 1024);
            var errorText = Limit(await stderr, 32 * 1024);
            return new ToolResult
            {
                Success = process.ExitCode == 0,
                Data = new { exitCode = process.ExitCode, stdout = output, stderr = errorText },
                DisplayText = string.IsNullOrWhiteSpace(output) ? errorText : output,
                Error = process.ExitCode == 0 ? null : $"Command exited with code {process.ExitCode}: {errorText}"
            };
        }));
        registry.Register(new DelegateTool("http_get", "http_get", "读取 HTTP/HTTPS 资源。", UrlSchema(), async args =>
        {
            var value = ReadRequired(args, "url");
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("url must use http or https.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var response = await client.GetAsync(uri);
            var body = Limit(await response.Content.ReadAsStringAsync(), 128 * 1024);
            return new ToolResult
            {
                Success = response.IsSuccessStatusCode,
                Data = new { status = (int)response.StatusCode, contentType = response.Content.Headers.ContentType?.ToString(), body },
                DisplayText = body,
                Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}"
            };
        }));
        registry.Register(new DelegateTool("send_gateway_file", "send_gateway_file", "发送 workspace 内文件；channel 直接上传，passthrough 生成链接。", FileSchema(), args =>
        {
            var path = ResolvePath(workspace, ReadRequired(args, "path"));
            if (!File.Exists(path)) return Task.FromResult(new ToolResult { Success = false, Error = $"File not found: {path}" });
            var mode = string.Equals(ReadOptional(args, "deliveryMode"), "passthrough", StringComparison.OrdinalIgnoreCase) ? "passthrough" : "channel";
            var name = ReadOptional(args, "name") ?? Path.GetFileName(path);
            var expiry = ReadOptionalInt(args, "expiryMinutes") ?? 30;
            var attachment = new { path, name, deliveryMode = mode, expiryMinutes = mode == "passthrough" ? expiry : (int?)null };
            return Task.FromResult(new ToolResult
            {
                Success = true,
                Data = attachment,
                DisplayText = mode == "passthrough" ? $"已准备文件下载链接：{name}" : $"已准备上传文件：{name}",
                Attachments = [attachment]
            });
        }));
        registry.Register(new DelegateTool("get_gateway_context", "get_gateway_context", "读取当前请求的脱敏平台上下文。", "{\"type\":\"object\",\"additionalProperties\":false}", _ =>
            Task.FromResult(new ToolResult { Success = true, Data = context, DisplayText = string.Join('\n', context.Select(item => $"{item.Key}: {item.Value}")) })));
        await HostMcpToolLoader.RegisterAsync(registry, mcpServers, ct);
        return registry;
    }

    private static string ResolvePath(string workspace, string value)
    {
        var root = Path.GetFullPath(workspace);
        var path = Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.Equals(root, StringComparison.Ordinal) && !path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Path is outside the configured workspace.");
        return path;
    }

    private static string ReadRequired(string json, string name)
        => ReadOptional(json, name) ?? throw new InvalidOperationException($"Missing required argument: {name}");

    private static string? ReadOptional(string json, string name)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadOptionalInt(string json, string name)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static string PathSchema() => "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}";
    private static string WriteSchema() => "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}},\"required\":[\"path\",\"content\"]}";
    private static string FileSchema() => "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"deliveryMode\":{\"type\":\"string\",\"enum\":[\"channel\",\"passthrough\"]},\"expiryMinutes\":{\"type\":\"integer\"}},\"required\":[\"path\",\"deliveryMode\"]}";
    private static string CommandSchema() => "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":900}},\"required\":[\"command\"]}";
    private static string UrlSchema() => "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\",\"format\":\"uri\"}},\"required\":[\"url\"]}";
    private static string Quote(string value) => $"'{value.Replace("'", "'\\''")}'";
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "\n[truncated]";
}

internal sealed class DelegateTool(
    string id,
    string name,
    string description,
    string schema,
    Func<string, Task<ToolResult>> execute) : ITool
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string ParametersSchema { get; } = schema;
    public Task<ToolResult> ExecuteAsync(string arguments) => execute(arguments);
    public Task<bool> IsAvailableAsync() => Task.FromResult(true);
}

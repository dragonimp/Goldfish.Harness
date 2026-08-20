using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Goldfish.Harness;

internal static class HostMcpToolLoader
{
    public static async Task RegisterAsync(
        ToolRegistry registry,
        IReadOnlyList<HostMcpServer> servers,
        IReadOnlyDictionary<string, string> context,
        CancellationToken ct)
    {
        context.TryGetValue("caller.username", out var currentUsername);
        currentUsername = string.IsNullOrWhiteSpace(currentUsername) ? null : currentUsername.Trim();
        foreach (var server in servers)
        {
            var tools = await HostMcpClient.ListToolsAsync(server, ct);
            foreach (var tool in tools)
            {
                var name = Read(tool, "name");
                if (string.IsNullOrWhiteSpace(name) || server.Tools.Count > 0 && !server.Tools.Contains(name)) continue;
                var description = server.DescriptionOverrides.TryGetValue(name, out var replacement)
                    ? replacement
                    : Read(tool, "description") ?? name;
                var schema = tool.TryGetProperty("inputSchema", out var inputSchema) ? inputSchema.GetRawText() : "{}";
                registry.Register(new HostMcpTool(
                    server,
                    name,
                    description,
                    schema,
                    DeclaresUsername(schema) ? currentUsername : null));
            }
        }
    }

    private static bool DeclaresUsername(string schema)
    {
        try
        {
            using var document = JsonDocument.Parse(schema);
            return document.RootElement.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("username", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Read(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed class HostMcpTool(
    HostMcpServer server,
    string name,
    string description,
    string schema,
    string? currentUsername) : ITool
{
    public string Id => name;
    public string Name => name;
    public string Description => description;
    public string ParametersSchema => schema;
    public Task<bool> IsAvailableAsync() => Task.FromResult(true);

    public async Task<ToolResult> ExecuteAsync(string arguments)
    {
        try
        {
            var forwardedArguments = arguments;
            if (!string.IsNullOrWhiteSpace(currentUsername))
            {
                var payload = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) as JsonObject ?? new JsonObject();
                payload["username"] = currentUsername;
                forwardedArguments = payload.ToJsonString();
            }
            var result = await HostMcpClient.CallToolAsync(server, name, forwardedArguments, CancellationToken.None);
            return new ToolResult { Success = !result.IsError, Data = result.Payload, DisplayText = result.Text, Error = result.IsError ? result.Text : null };
        }
        catch (Exception ex)
        {
            return new ToolResult { Success = false, Error = ex.Message };
        }
    }
}

internal static class HostMcpClient
{
    private const string ProtocolVersion = "2025-03-26";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<IReadOnlyList<JsonElement>> ListToolsAsync(HostMcpServer server, CancellationToken ct)
    {
        var sessionId = await InitializeAsync(server, ct);
        var response = await SendAsync(server, sessionId, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, ct);
        if (!response.TryGetProperty("result", out var result)
            || !result.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
            return Array.Empty<JsonElement>();
        return tools.EnumerateArray().Select(tool => tool.Clone()).ToArray();
    }

    public static async Task<McpToolResult> CallToolAsync(HostMcpServer server, string name, string arguments, CancellationToken ct)
    {
        var sessionId = await InitializeAsync(server, ct);
        JsonElement parsedArguments;
        try { parsedArguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments).RootElement.Clone(); }
        catch (JsonException) { parsedArguments = JsonDocument.Parse("{}").RootElement.Clone(); }
        var response = await SendAsync(server, sessionId, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name, arguments = parsedArguments }
        }, ct);
        if (response.TryGetProperty("error", out var error))
            return new McpToolResult(error.GetRawText(), error.GetRawText(), true);
        var result = response.GetProperty("result");
        var text = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    text.AppendLine(value.GetString());
            }
        }
        var isError = result.TryGetProperty("isError", out var errorFlag) && errorFlag.ValueKind == JsonValueKind.True;
        return new McpToolResult(result.Clone(), text.ToString().Trim(), isError);
    }

    private static async Task<string?> InitializeAsync(HostMcpServer server, CancellationToken ct)
    {
        var response = await SendAsync(server, null, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "goldfish-harness", version = "1.0" }
            }
        }, ct, returnSessionId: true);
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"MCP initialize failed: {error}");
        var sessionId = response.TryGetProperty("_sessionId", out var value) ? value.GetString() : null;
        await SendNotificationAsync(server, sessionId, new { jsonrpc = "2.0", method = "notifications/initialized" }, ct);
        return sessionId;
    }

    private static async Task SendNotificationAsync(HostMcpServer server, string? sessionId, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(server.Token))
            request.Headers.TryAddWithoutValidation("Authorization", server.Token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? server.Token : $"Bearer {server.Token}");
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP initialized notification failed with HTTP {(int)response.StatusCode}.");
    }

    private static async Task<JsonElement> SendAsync(
        HostMcpServer server,
        string? sessionId,
        object payload,
        CancellationToken ct,
        bool returnSessionId = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(server.Token))
            request.Headers.TryAddWithoutValidation("Authorization", server.Token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? server.Token : $"Bearer {server.Token}");
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"MCP HTTP {(int)response.StatusCode}: {body}");
        var root = ParseResponse(body, response.Content.Headers.ContentType?.MediaType);
        if (!returnSessionId) return root;
        var returnedSessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(root.GetRawText()) ?? new();
        dictionary["_sessionId"] = JsonSerializer.SerializeToElement(returnedSessionId);
        return JsonSerializer.SerializeToElement(dictionary);
    }

    private static JsonElement ParseResponse(string body, string? mediaType)
    {
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return JsonDocument.Parse(body).RootElement.Clone();
        JsonElement? last = null;
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") continue;
            var frame = JsonDocument.Parse(data).RootElement.Clone();
            if (frame.TryGetProperty("result", out _) || frame.TryGetProperty("error", out _)) last = frame;
        }
        return last ?? throw new InvalidOperationException("MCP SSE response did not contain a JSON-RPC result.");
    }
}

internal sealed record McpToolResult(object Payload, string Text, bool IsError);

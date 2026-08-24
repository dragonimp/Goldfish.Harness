using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Goldfish.Harness;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class AcpHostProcessTests
{
    [Fact]
    public async Task IndependentProcess_CompletesInitializeSessionAndPrompt()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var server = FakeOpenAiServer.Start("hello from independent harness");
        var workspace = Directory.CreateTempSubdirectory("goldfish-acp-workspace-");
        var state = Directory.CreateTempSubdirectory("goldfish-acp-state-");

        try
        {
            using var process = StartHost();
            await SendAsync(process, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
            var initialize = await ReadUntilResponseAsync(process, 1, timeout.Token);
            Assert.Equal(1, initialize.GetProperty("result").GetProperty("protocolVersion").GetInt32());
            var goldfish = initialize.GetProperty("result").GetProperty("_meta").GetProperty("goldfish");
            Assert.Equal(SqliteHarnessStateStore.CurrentSchemaVersion, goldfish.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Dual", goldfish.GetProperty("stateMode").GetString());

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "session/new",
                @params = new
                {
                    cwd = workspace.FullName,
                    _meta = new
                    {
                        agentfree = new
                        {
                            requestedSessionId = "process-session",
                            runtime = new
                            {
                                baseUrl = server.BaseUrl,
                                apiKey = "test-key",
                                model = "test-model",
                                systemPrompt = "Answer concisely.",
                                stateRoot = state.FullName
                            }
                        }
                    }
                }
            });
            var session = await ReadUntilResponseAsync(process, 2, timeout.Token);
            Assert.Equal("process-session", session.GetProperty("result").GetProperty("sessionId").GetString());

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "session/prompt",
                @params = new
                {
                    sessionId = "process-session",
                    prompt = new[] { new { type = "text", text = "hello" } },
                    _meta = new
                    {
                        agentfree = new
                        {
                            turnId = "gateway-turn-1",
                            requestId = "gateway-request-1",
                            source = "gateway",
                            reasoning = new { strategy = "react" }
                        }
                    }
                }
            });

            var frames = await ReadUntilResponseWithFramesAsync(process, 3, timeout.Token);
            Assert.Equal("end_turn", frames.Response.GetProperty("result").GetProperty("stopReason").GetString());
            var message = Assert.Single(frames.Notifications, frame =>
                frame.GetProperty("params").GetProperty("update").GetProperty("sessionUpdate").GetString() == "agent_message_chunk");
            Assert.Equal(
                "hello from independent harness",
                message.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString());

            var requestCount = server.RequestCount;
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "session/prompt",
                @params = new
                {
                    sessionId = "process-session",
                    prompt = new[] { new { type = "text", text = "hello" } },
                    _meta = new { agentfree = new { turnId = "ignored-duplicate", requestId = "gateway-request-1" } }
                }
            });
            var replay = await ReadUntilResponseWithFramesAsync(process, 5, timeout.Token);
            Assert.Equal("end_turn", replay.Response.GetProperty("result").GetProperty("stopReason").GetString());
            Assert.Equal(requestCount, server.RequestCount);

            await using (var connection = new SqliteConnection($"Data Source={Path.Combine(state.FullName, "harness-state.db")}"))
            {
                await connection.OpenAsync(timeout.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT turn_id, request_id, status, strategy FROM goldfish_turns;";
                await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                Assert.True(await reader.ReadAsync(timeout.Token));
                Assert.Equal("gateway-turn-1", reader.GetString(0));
                Assert.Equal("gateway-request-1", reader.GetString(1));
                Assert.Equal("Completed", reader.GetString(2));
                Assert.Equal("ReAct", reader.GetString(3));
                Assert.False(await reader.ReadAsync(timeout.Token));
            }

            await SendAsync(process, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
            await ReadUntilResponseAsync(process, 4, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.True(server.RequestCount >= 1);
        }
        finally
        {
            workspace.Delete(recursive: true);
            state.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task IndependentProcess_RejectsRelativeWorkspace()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = StartHost();

        await SendAsync(process, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "session/new",
            @params = new
            {
                cwd = "relative/path",
                _meta = new { agentfree = new { runtime = new { } } }
            }
        });

        var response = await ReadUntilResponseAsync(process, 1, timeout.Token);
        Assert.Equal(-32603, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("absolute cwd", response.GetProperty("error").GetProperty("message").GetString());

        await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "shutdown", @params = new { } });
        await ReadUntilResponseAsync(process, 2, timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task IndependentProcess_RuntimeFailureUsesExtensionAndJsonRpcErrorTerminal()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var server = FakeOpenAiServer.StartFailure(HttpStatusCode.Unauthorized, "missing test credential");
        var workspace = Directory.CreateTempSubdirectory("goldfish-acp-failure-workspace-");
        var state = Directory.CreateTempSubdirectory("goldfish-acp-failure-state-");

        try
        {
            using var process = StartHost();
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "session/new",
                @params = new
                {
                    cwd = workspace.FullName,
                    _meta = new
                    {
                        agentfree = new
                        {
                            requestedSessionId = "failure-session",
                            runtime = new
                            {
                                baseUrl = server.BaseUrl,
                                apiKey = "invalid-test-key",
                                model = "test-model",
                                stateRoot = state.FullName
                            }
                        }
                    }
                }
            });
            await ReadUntilResponseAsync(process, 1, timeout.Token);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "session/prompt",
                @params = new
                {
                    sessionId = "failure-session",
                    prompt = new[] { new { type = "text", text = "fail" } }
                }
            });

            var frames = await ReadUntilResponseWithFramesAsync(process, 2, timeout.Token);
            Assert.Equal(-32603, frames.Response.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Contains("401", frames.Response.GetProperty("error").GetProperty("message").GetString());
            Assert.Single(frames.Notifications, frame =>
                frame.GetProperty("method").GetString() == "_agentfree/runtime.error");
            Assert.DoesNotContain(frames.Notifications, frame =>
                frame.GetProperty("method").GetString() == "session/update"
                && frame.GetProperty("params").GetProperty("update").GetProperty("sessionUpdate").GetString() == "agent_message_chunk");

            await SendAsync(process, new { jsonrpc = "2.0", id = 3, method = "shutdown", @params = new { } });
            await ReadUntilResponseAsync(process, 3, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            workspace.Delete(recursive: true);
            state.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task IndependentProcess_CancelsActivePrompt()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var server = FakeOpenAiServer.Start("too late", TimeSpan.FromSeconds(10));
        var workspace = Directory.CreateTempSubdirectory("goldfish-acp-cancel-workspace-");
        var state = Directory.CreateTempSubdirectory("goldfish-acp-cancel-state-");

        try
        {
            using var process = StartHost();
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "session/new",
                @params = new
                {
                    cwd = workspace.FullName,
                    _meta = new
                    {
                        agentfree = new
                        {
                            requestedSessionId = "cancel-session",
                            runtime = new { baseUrl = server.BaseUrl, model = "test-model", stateRoot = state.FullName }
                        }
                    }
                }
            });
            await ReadUntilResponseAsync(process, 1, timeout.Token);
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "session/prompt",
                @params = new { sessionId = "cancel-session", prompt = new[] { new { type = "text", text = "wait" } } }
            });

            while (server.RequestCount == 0)
                await Task.Delay(10, timeout.Token);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "session/cancel",
                @params = new { sessionId = "cancel-session" }
            });

            var responses = await ReadResponsesAsync(process, new HashSet<int> { 2, 3 }, timeout.Token);
            Assert.True(responses[3].GetProperty("result").GetProperty("cancelled").GetBoolean());
            Assert.Equal("cancelled", responses[2].GetProperty("result").GetProperty("stopReason").GetString());

            await SendAsync(process, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
            await ReadUntilResponseAsync(process, 4, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            workspace.Delete(recursive: true);
            state.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task IndependentProcess_CancelResponseIsBarrierForNextPrompt()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var server = FakeOpenAiServer.Start("completed after cancellation", TimeSpan.FromSeconds(2));
        var workspace = Directory.CreateTempSubdirectory("goldfish-acp-cancel-barrier-workspace-");
        var state = Directory.CreateTempSubdirectory("goldfish-acp-cancel-barrier-state-");

        try
        {
            using var process = StartHost();
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "session/new",
                @params = new
                {
                    cwd = workspace.FullName,
                    _meta = new
                    {
                        agentfree = new
                        {
                            requestedSessionId = "cancel-barrier-session",
                            runtime = new { baseUrl = server.BaseUrl, model = "test-model", stateRoot = state.FullName }
                        }
                    }
                }
            });
            await ReadUntilResponseAsync(process, 1, timeout.Token);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "session/prompt",
                @params = new { sessionId = "cancel-barrier-session", prompt = new[] { new { type = "text", text = "wait" } } }
            });
            while (server.RequestCount == 0)
                await Task.Delay(10, timeout.Token);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "session/cancel",
                @params = new { sessionId = "cancel-barrier-session" }
            });
            var cancel = await ReadUntilResponseAsync(process, 3, timeout.Token);
            Assert.True(cancel.GetProperty("result").GetProperty("cancelled").GetBoolean());

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "session/prompt",
                @params = new { sessionId = "cancel-barrier-session", prompt = new[] { new { type = "text", text = "continue" } } }
            });
            var nextPrompt = await ReadUntilResponseAsync(process, 4, timeout.Token);
            Assert.False(nextPrompt.TryGetProperty("error", out _));
            Assert.Equal("end_turn", nextPrompt.GetProperty("result").GetProperty("stopReason").GetString());

            await SendAsync(process, new { jsonrpc = "2.0", id = 5, method = "shutdown", @params = new { } });
            await ReadUntilResponseAsync(process, 5, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            workspace.Delete(recursive: true);
            state.Delete(recursive: true);
        }
    }

    private static Process StartHost()
    {
        var assembly = typeof(AcpHostAssembly).Assembly.Location;
        return Process.Start(new ProcessStartInfo("dotnet", $"\"{assembly}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start ACP host process.");
    }

    private static async Task SendAsync(Process process, object frame)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(frame));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadUntilResponseAsync(Process process, int id, CancellationToken ct)
        => (await ReadUntilResponseWithFramesAsync(process, id, ct)).Response;

    private static async Task<(JsonElement Response, IReadOnlyList<JsonElement> Notifications)> ReadUntilResponseWithFramesAsync(
        Process process,
        int id,
        CancellationToken ct)
    {
        var notifications = new List<JsonElement>();
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"ACP host exited before response {id}: {error}");
            }

            var frame = JsonDocument.Parse(line).RootElement.Clone();
            if (frame.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
                return (frame, notifications);
            if (frame.TryGetProperty("method", out _))
                notifications.Add(frame);
        }
    }

    private static async Task<IReadOnlyDictionary<int, JsonElement>> ReadResponsesAsync(
        Process process,
        IReadOnlySet<int> ids,
        CancellationToken ct)
    {
        var responses = new Dictionary<int, JsonElement>();
        while (responses.Count < ids.Count)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct)
                ?? throw new InvalidOperationException("ACP host exited before all responses were received.");
            var frame = JsonDocument.Parse(line).RootElement.Clone();
            if (frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && ids.Contains(id.GetInt32()))
                responses[id.GetInt32()] = frame;
        }
        return responses;
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _answer;
        private readonly TimeSpan _responseDelay;
        private readonly HttpStatusCode? _failureStatusCode;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serveTask;
        private int _requestCount;

        private FakeOpenAiServer(
            TcpListener listener,
            string answer,
            TimeSpan responseDelay,
            HttpStatusCode? failureStatusCode = null)
        {
            _listener = listener;
            _answer = answer;
            _responseDelay = responseDelay;
            _failureStatusCode = failureStatusCode;
            _serveTask = ServeAsync();
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        public int RequestCount => Volatile.Read(ref _requestCount);

        public static FakeOpenAiServer Start(string answer, TimeSpan responseDelay = default)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeOpenAiServer(listener, answer, responseDelay);
        }

        public static FakeOpenAiServer StartFailure(HttpStatusCode statusCode, string message)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeOpenAiServer(listener, message, default, statusCode);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    try
                    {
                        await HandleAsync(client, _stop.Token);
                    }
                    catch (IOException) when (!_stop.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? line;
            var contentLength = 0;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line["Content-Length:".Length..].Trim());
            }
            if (contentLength > 0)
            {
                var body = new char[contentLength];
                await reader.ReadBlockAsync(body, ct);
            }
            Interlocked.Increment(ref _requestCount);
            if (_responseDelay > TimeSpan.Zero)
                await Task.Delay(_responseDelay, ct);

            if (_failureStatusCode.HasValue)
            {
                var failureBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = _answer }));
                var failureHeaders = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(int)_failureStatusCode.Value} {_failureStatusCode.Value}\r\nContent-Type: application/json\r\nContent-Length: {failureBody.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(failureHeaders, ct);
                await stream.WriteAsync(failureBody, ct);
                return;
            }

            var first = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = _answer }, finish_reason = (string?)null } } });
            var second = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } });
            var payload = $"data: {first}\n\ndata: {second}\n\ndata: [DONE]\n\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, ct);
            await stream.WriteAsync(bytes, ct);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _serveTask;
            _stop.Dispose();
        }
    }
}

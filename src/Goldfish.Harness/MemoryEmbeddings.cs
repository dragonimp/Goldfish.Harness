using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goldfish.Harness;

/// <summary>
/// OpenAI-compatible embedding endpoint configuration used by the memory subsystem.
/// Configuration providers can bind this object from the <c>Goldfish:Memory:Embedding</c> section.
/// </summary>
public sealed class MemoryEmbeddingOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://127.0.0.1:18790/v1/embeddings";
    public string Model { get; set; } = "qmd-embeddinggemma-300m";
    public int Dimensions { get; set; } = 768;
    public int TimeoutSeconds { get; set; } = 30;
    public string? ApiKey { get; set; }
    public string QueryInputType { get; set; } = "query";
    public string DocumentInputType { get; set; } = "document";
    public double MinimumSimilarity { get; set; } = 0.25;
    public bool FallbackToLexicalSearch { get; set; } = true;
}

public enum MemoryEmbeddingInputType
{
    Query,
    Document
}

public interface IMemoryEmbeddingClient
{
    Task<IReadOnlyList<float>> GenerateAsync(
        string text,
        MemoryEmbeddingInputType inputType,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal OpenAI-compatible <c>/v1/embeddings</c> client. It also emits the
/// <c>x-embedding-input-type</c> hint understood by the local QMD server.
/// </summary>
public sealed class OpenAiCompatibleMemoryEmbeddingClient : IMemoryEmbeddingClient, IDisposable
{
    private readonly MemoryEmbeddingOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public OpenAiCompatibleMemoryEmbeddingClient(
        MemoryEmbeddingOptions options,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            throw new ArgumentException("Memory embedding is disabled.", nameof(options));
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _))
            throw new ArgumentException("A valid absolute embedding endpoint is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("An embedding model is required.", nameof(options));

        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
    }

    public async Task<IReadOnlyList<float>> GenerateAsync(
        string text,
        MemoryEmbeddingInputType inputType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new EmbeddingRequest(_options.Model, text))
        };
        request.Headers.TryAddWithoutValidation(
            "x-embedding-input-type",
            inputType == MemoryEmbeddingInputType.Query
                ? _options.QueryInputType
                : _options.DocumentInputType);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Embedding endpoint returned {(int)response.StatusCode}: {Truncate(payload, 500)}",
                null,
                response.StatusCode);
        }

        EmbeddingResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<EmbeddingResponse>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Embedding endpoint returned invalid JSON.", ex);
        }

        var embedding = result?.Data?.OrderBy(item => item.Index).FirstOrDefault()?.Embedding;
        if (embedding is not { Count: > 0 })
            throw new InvalidOperationException("Embedding endpoint returned an empty vector.");
        if (_options.Dimensions > 0 && embedding.Count != _options.Dimensions)
        {
            throw new InvalidOperationException(
                $"Embedding dimension mismatch: expected {_options.Dimensions}, got {embedding.Count}.");
        }

        return embedding;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData>? Data { get; init; }
    }

    private sealed class EmbeddingData
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("embedding")]
        public List<float>? Embedding { get; init; }
    }
}

using System.Text.Json;

namespace Goldfish.Harness;

public static class GoldfishLlmHeaders
{
    public static IReadOnlyDictionary<string, string> Build(Dictionary<string, JsonElement>? metadata)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-LLMFree-Agent-Type"] = "Goldfish"
        };

        AddHeader(headers, "X-LLMFree-App-Request-Id", FirstNonEmpty(
            GetMetadataString(metadata, "request_id", "requestId", "RequestId"),
            GetMetadataString(metadata, "AppRequestId", "ApplicationRequestId", "appRequestId", "applicationRequestId"),
            GetMetadataString(metadata, "GatewayRequestId", "gatewayRequestId", "gateway_request_id"),
            GetMetadataString(metadata, "GatewayMetadata_request_id", "GatewayMetadata_requestId"),
            GetMetadataString(metadata, "GatewayMetadata_replyReqId", "GatewayMetadata_contextToken"),
            GetMetadataString(metadata, "GatewayMetadata_traceId")));

        AddHeader(headers, "X-LLMFree-App-User", FirstNonEmpty(
            GetMetadataString(metadata, "AppUserId", "ApplicationUserId", "appUserId", "applicationUserId"),
            GetMetadataString(metadata, "GatewayUserId", "GatewayMetadata_userId", "GatewayMetadata_userName"),
            GetMetadataString(metadata, "UserId", "userId")));

        AddHeader(headers, "X-LLMFree-Gateway-Type", GetMetadataString(metadata, "GatewayType"));
        AddHeader(headers, "X-LLMFree-Session-Id", GetMetadataString(metadata, "session_id", "SessionId"));
        return headers;
    }

    private static void AddHeader(Dictionary<string, string> headers, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        headers[name] = ToAsciiHeaderValue(value.Trim());
    }

    private static string ToAsciiHeaderValue(string value)
    {
        foreach (var ch in value)
        {
            if (ch < 0x20 || ch > 0x7E)
                return Uri.EscapeDataString(value);
        }

        return value;
    }

    private static string? GetMetadataString(Dictionary<string, JsonElement>? metadata, params string[] keys)
    {
        if (metadata is null) return null;
        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, out var value)) continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }
}

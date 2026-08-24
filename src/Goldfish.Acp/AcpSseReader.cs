using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Goldfish.Acp;

public static class AcpSseReader
{
    public static async IAsyncEnumerable<AcpInboundEnvelope> ReadAsync(
        Stream stream,
        StringBuilder? raw = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            raw?.AppendLine(line);
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var json = trimmed[5..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;
            AcpInboundEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<AcpInboundEnvelope>(json, AcpJson.Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Invalid ACP JSON-RPC SSE frame.", ex);
            }
            if (envelope != null) yield return envelope;
        }
    }
}

using System.Text.Json;

namespace Goldfish.Harness;

public sealed record HarnessImportReport(int ImportedTurns, int ImportedEvents, int SkippedLines, int MalformedLines);

public static class JsonlHarnessImporter
{
    public static async Task<HarnessImportReport> ImportAsync(
        string ledgerPath,
        IHarnessRuntimeStore target,
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        if (!File.Exists(ledgerPath)) return new HarnessImportReport(0, 0, 0, 0);
        var turns = new Dictionary<string, ImportTurn>(StringComparer.Ordinal);
        var malformed = 0;
        await foreach (var line in File.ReadLinesAsync(ledgerPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "turn.started" && root.TryGetProperty("turn", out var turnElement))
                {
                    var turn = turnElement.Deserialize<GoldfishHarnessTurn>(HarnessRuntimeJson.Options);
                    if (turn is null || turn.CreatedAt < cutoff) continue;
                    var requestId = turnElement.TryGetProperty("requestId", out var requestElement)
                        ? requestElement.GetString()
                        : null;
                    turn = turn with
                    {
                        RequestId = string.IsNullOrWhiteSpace(requestId) ? $"legacy-{turn.TurnId}" : requestId,
                        Status = GoldfishTurnStatus.Queued
                    };
                    turns.TryAdd(turn.TurnId, new ImportTurn(turn));
                }
                else if (type == "turn.event" && root.TryGetProperty("turnEvent", out var eventElement))
                {
                    var turnEvent = eventElement.Deserialize<GoldfishHarnessTurnEvent>(HarnessRuntimeJson.Options);
                    if (turnEvent is not null && turns.TryGetValue(turnEvent.TurnId, out var state))
                        state.Events.Add(turnEvent.Event);
                }
                else if (type == "turn.completed" && root.TryGetProperty("turnId", out var turnIdElement)
                    && turns.TryGetValue(turnIdElement.GetString() ?? string.Empty, out var state))
                {
                    state.Status = root.TryGetProperty("status", out var statusElement)
                        ? ParseStatus(statusElement)
                        : GoldfishTurnStatus.Failed;
                    state.TerminalReason = root.TryGetProperty("terminalReason", out var reasonElement)
                        ? reasonElement.GetString()
                        : null;
                }
            }
            catch (JsonException)
            {
                malformed++;
            }
        }

        var importedTurns = 0;
        var importedEvents = 0;
        var skipped = 0;
        foreach (var state in turns.Values.OrderBy(item => item.Turn.CreatedAt))
        {
            ct.ThrowIfCancellationRequested();
            if (await target.GetTurnAsync(state.Turn.TurnId, ct) is not null)
            {
                skipped++;
                continue;
            }
            var created = await target.GetOrCreateTurnAsync(state.Turn, string.Empty, ct);
            if (!created.Created)
            {
                skipped++;
                continue;
            }
            importedTurns++;
            await target.TryStartAsync(state.Turn.TurnId, "jsonl-import", DateTimeOffset.UtcNow.AddMinutes(1), ct);
            if (state.Events.Count > 0)
            {
                await target.AppendEventsAsync(state.Turn.TurnId, state.Turn.SessionId, state.Events, ct);
                importedEvents += state.Events.Count;
            }
            var terminal = state.Status is GoldfishTurnStatus.Completed or GoldfishTurnStatus.Failed
                or GoldfishTurnStatus.Canceled or GoldfishTurnStatus.Orphaned
                ? state.Status
                : GoldfishTurnStatus.Orphaned;
            await target.TryCompleteAsync(state.Turn.TurnId, terminal, "jsonl_import",
                state.TerminalReason ?? "Imported from the legacy JSONL ledger.",
                terminal == GoldfishTurnStatus.Completed
                    ? state.Events.LastOrDefault(ev => ev.Kind == GoldfishEventKind.Completed)?.Delta
                    : null,
                ct);
        }
        return new HarnessImportReport(importedTurns, importedEvents, skipped, malformed);
    }

    private static GoldfishTurnStatus ParseStatus(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            && Enum.IsDefined(typeof(GoldfishTurnStatus), number))
            return (GoldfishTurnStatus)number;
        if (value.ValueKind == JsonValueKind.String
            && Enum.TryParse<GoldfishTurnStatus>(value.GetString(), true, out var status))
            return status;
        return GoldfishTurnStatus.Failed;
    }

    private sealed class ImportTurn(GoldfishHarnessTurn turn)
    {
        public GoldfishHarnessTurn Turn { get; } = turn;
        public List<GoldfishHarnessEvent> Events { get; } = [];
        public GoldfishTurnStatus Status { get; set; } = GoldfishTurnStatus.Orphaned;
        public string? TerminalReason { get; set; }
    }
}

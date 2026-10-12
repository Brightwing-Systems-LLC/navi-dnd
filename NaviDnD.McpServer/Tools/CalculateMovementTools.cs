using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using ModelContextProtocol.Server;
using NaviDnD;

namespace NaviDnD.McpServer;

[McpServerToolType]
public sealed class CalculateMovementTools(McpServerConfig config)
{
    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object _logLock = new();

    [McpServerTool]
    [Description("""
        Computes a movement path for one entity from a prioritized intent list. Returns the first
        reachable intent with the full path and the triggers along it.

        Request schema:
          entityId: int                   — index in map.entities
          reachFt?: int                   — the hero's reach in feet, for detecting opportunity attacks (default 5)
          fromPosition?: [col, row]       — overrides the entity's current position (for a repeat call after a partial path)
          speedFt?: int                   — overrides the entity's SpeedMax (pass remainingSpeedFt from the previous call on a repeat call)
          skipOpportunityAttack?: bool    — true on the second call after the opportunity attack has been resolved (default false)
          intents: [
            { intent: "approach",          target: "hero" }
            { intent: "maintain_distance", target: "hero", minFt: int, maxFt: int }
            { intent: "flee",              from: "hero" | "[col,row]" }
            { intent: "stay" }
          ]

        Always include { intent: "stay" } as the last element — it is always reachable.

        Response:
          usedIntent: string           — which intent from the list was used
          steps: [[col,row], ...]      — the full path, including the starting position
          remainingSpeedFt: int        — speed left after this move (pass it as speedFt on a repeat call)
          triggers: [{ atStep, type, pausePosition?, objectName?, effect? }, ...]
            types: "opportunity_attack" | "onStep" | "area_enter"
        """)]
    public string CalculateMovement(
        [Description("MovementRequest as a JSON string")]
        string requestJson)
    {
        Log($"calculate_movement → {requestJson}");
        try
        {
            var req = JsonSerializer.Deserialize<MovementRequest>(requestJson, _readOptions)
                      ?? throw new ArgumentException("Invalid request JSON");

            var ws     = LoadWorldState();
            var result = MovementCalculator.Calculate(ws, req);
            string resultJson = JsonSerializer.Serialize(result, _writeOptions);

            Log($"calculate_movement ← entity:{req.EntityId} intent:{result.UsedIntent} steps:{result.Steps.Count} remaining:{result.RemainingSpeedFt}ft triggers:{result.Triggers.Count} ({resultJson.Length} chars): {resultJson}");
            return resultJson;
        }
        catch (Exception ex)
        {
            Log($"calculate_movement ← (ERROR) {ex.Message}");
            return Error(ex.Message);
        }
    }

    // ── IO helpers ──────────────────────────────────────────────────────────

    private WorldState LoadWorldState()
    {
        string json = RetryOnIo(() =>
        {
            using var stream = new FileStream(config.WorldStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });
        var storage = new Storage();
        storage.ApplySavedState(json);
        return storage.WorldState;
    }

    private static T RetryOnIo<T>(Func<T> action, int attempts = 5, int delayMs = 30)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return action(); }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(delayMs); }
        }
        throw new UnreachableException();
    }

    private static string Error(string message) => $"{{\"error\":\"{message}\"}}";

    private void Log(string message)
    {
        if (config.LogPath is null) return;
        lock (_logLock)
            File.AppendAllText(config.LogPath, $"  MCP calculate_movement {message}\n", new UTF8Encoding(false));
    }
}

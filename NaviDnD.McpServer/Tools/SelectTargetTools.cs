using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using NaviDnD;

namespace NaviDnD.McpServer;

[McpServerToolType]
public sealed class SelectTargetTools(McpServerConfig config)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object _logLock = new();

    private string RequestPath =>
        Path.Combine(Path.GetDirectoryName(config.WorldStatePath)!, "select_target_request.json");

    [McpServerTool]
    [Description("""
        Pick a target or a place with the mouse on the map. Any area effect (sphere/cube/cone/line) — always call
        it, with shape and sizeFt, even if the place is named in words: the player must see and choose the area.
        A single target — only if it's unclear exactly who (several fitting creatures); if unambiguous, don't call it.
        The player sees the valid cells highlighted and a preview of the area; waits up to 50 seconds for an answer.
        Fields:
        - history[]: entries before the choice, as in use_reaction. The last entry = what to choose ("Where do you aim the Fireball?").
        - mode: "creature" — choose a creature (only those visible to the hero; the hero themselves is allowed), "point" — choose a cell (explored, in line of sight).
        - rangeFt: maximum distance from the hero in feet. Omit for no limit.
        - count: how many targets to choose (default 1). allowRepeat: whether the same target can be chosen several times.
        - shape: the area's shape around the chosen point — "sphere" (radius sizeFt), "cube" (side sizeFt), or from the hero toward the chosen cell — "cone" (length sizeFt), "line" (length sizeFt, width 5 ft). No area — omit.
        - sizeFt: the area's size in feet.
        Returns { "targets": [{ "id", "symbol", "name", "position", "distanceFt" }] }; for an area also
        "areaCells" (all cells [col,row]), "inArea" (creatures, including the hero), "objectsInArea" (map objects).
        id — the index in map.entities / map.objects for a patch (the hero has none).
        Player cancelled — { "cancelled": true }; no answer within 50 seconds — { "timedOut": true }: the action doesn't happen, describe that briefly.
        """)]
    public string SelectTarget(
        [Description("Dialog entries before the choice. Each has text; text may be SSML <speak> for voice-over; optional author, patch.")] RequestHistoryEntry[] history,
        [Description("\"creature\" or \"point\".")] string mode,
        [Description("Maximum distance from the hero in feet.")] int? rangeFt = null,
        [Description("How many targets to choose.")] int count = 1,
        [Description("Whether the same target can be chosen several times.")] bool allowRepeat = false,
        [Description("\"sphere\", \"cube\", \"cone\" or \"line\".")] string? shape = null,
        [Description("Area size in feet.")] int? sizeFt = null)
    {
        var request = new TargetSelectionRequest
        {
            History = history?.ToList() ?? [],
            Mode = mode == "point" ? "point" : "creature",
            RangeFt = rangeFt,
            Count = Math.Max(1, count),
            AllowRepeat = allowRepeat,
            Shape = string.IsNullOrWhiteSpace(shape) ? null : shape.Trim().ToLowerInvariant(),
            SizeFt = sizeFt,
        };

        Log($"select_target → mode={request.Mode} range={rangeFt} count={request.Count} shape={request.Shape} size={sizeFt}");
        WriteRequest(request);

        var deadline = DateTime.UtcNow.AddSeconds(50);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            var current = TryReadRequest();
            if (current is { Answered: true, Result: not null })
            {
                TryDelete();
                string json = JsonSerializer.Serialize(current.Result, _json);
                Log($"select_target ← {json}");
                return json;
            }
        }

        TryDelete();
        Log("select_target ← timedOut");
        return JsonSerializer.Serialize(new { timedOut = true }, _json);
    }

    private void WriteRequest(TargetSelectionRequest request)
    {
        string json = JsonSerializer.Serialize(request, _json);
        RetryOnIo(() => File.WriteAllText(RequestPath, json, new UTF8Encoding(false)));
    }

    private TargetSelectionRequest? TryReadRequest()
    {
        try
        {
            if (!File.Exists(RequestPath)) return null;
            string json = RetryOnIo(() =>
            {
                using var stream = new FileStream(RequestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            });
            return JsonSerializer.Deserialize<TargetSelectionRequest>(json, _json);
        }
        catch { return null; }
    }

    private void TryDelete()
    {
        try { if (File.Exists(RequestPath)) File.Delete(RequestPath); }
        catch { }
    }

    private static T RetryOnIo<T>(Func<T> action, int attempts = 5, int delayMs = 30)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return action(); }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(delayMs); }
        }
        throw new InvalidOperationException("IO retry exhausted");
    }

    private static void RetryOnIo(Action action, int attempts = 5, int delayMs = 30)
        => RetryOnIo<object?>(() => { action(); return null; }, attempts, delayMs);

    private void Log(string message)
    {
        if (config.LogPath is null) return;
        lock (_logLock)
            File.AppendAllText(config.LogPath, $"  MCP {message}\n", new UTF8Encoding(false));
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ModelContextProtocol.Server;
using NaviDnD;
using NaviDnD.Data.Models;

namespace NaviDnD.McpServer;

// Mechanical patch helpers: round/rest/doors — deterministic bookkeeping that the AI used to have to
// reproduce flawlessly from the prompt's text rules. The source of at least
// two real bugs: a forgotten speedMax when removing an effect (§ CONDITIONS) and a door opened without
// checking that the hero is next to it (§ MAP AND OBJECTS). Each tool only RETURNS a piece of a patch and
// writes nothing itself — McpServer is a separate process that reads worldState.json from disk, while the game
// keeps WorldState in memory and patches it from the AI's JSON response without rereading the file; writing directly
// here would desync the processes.
[McpServerToolType]
public sealed class GameActionTools(McpServerConfig config)
{
    // Deliberately without DefaultIgnoreCondition.WhenWritingNull: "break" must return color:null
    // explicitly (the engine distinguishes "field missing" from "field = null" — see Storage.ApplyMapUpdate),
    // rather than losing it to a global ignore of null fields.
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly string[] _partsOfDay = PartsOfDay.Cycle;

    private static readonly object _logLock = new();

    [McpServerTool]
    [Description("""
        Advances the round: totalRounds+1, resets actions. triggeringActionId = the id of the resource that
        reached 0 this turn (→maxValue-1, the rest →maxValue); omit for a full reset
        (passing the turn in combat). Returns a patch ("time"+"hero") to merge — add combat.currentTurn
        yourself if needed.
        In combat (combat.active==true): call ONLY when the hero's turn is completely over (Action/
        Bonus Action/speed spent, or the hero explicitly ended the turn) AND the hero is LAST in
        combat.initiative (lowest score) — then the hero closes the round. Otherwise the round is closed by
        whichever enemy is last in combat.initiative, in the final entry of its turn — never in the middle of
        a turn and never just because one resource reached 0 while others remain.
        """)]
    public string AdvanceRound(
        [Description("id of the resource that reached 0 this turn, or omit for a full reset")]
        int? triggeringActionId)
    {
        Log($"advance_round → triggeringActionId:{triggeringActionId?.ToString() ?? "null"}");
        try
        {
            var ws = LoadWorldState();
            var hero = ws.Hero ?? throw new InvalidOperationException("hero missing");
            var actions = hero.Actions ?? throw new InvalidOperationException("hero.actions missing");

            var actionPatches = new List<object>();
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Deleted == true) continue;
                int value = triggeringActionId == i ? actions[i].MaxValue - 1 : actions[i].MaxValue;
                actionPatches.Add(new { id = i, value });
            }

            var result = new
            {
                time = new { totalRounds = ws.Time.TotalRounds + 1 },
                hero = new { actions = actionPatches, speedLeft = hero.SpeedMax },
            };
            string resultJson = JsonSerializer.Serialize(result, _writeOptions);
            Log($"advance_round ← ({resultJson.Length} chars): {resultJson}");
            return resultJson;
        }
        catch (Exception ex)
        {
            Log($"advance_round ← (ERROR) {ex.Message}");
            return Error(ex.Message);
        }
    }

    [McpServerTool]
    [Description("""
        Long Rest (call only if you've already decided it's allowed — no active damaging
        onRound effect): partOfDay+2, day+1 when passing through night, totalRounds→1, resets
        actions to maxValue and speedLeft to speedMax, removes effects with expiresAtRound+untilLongRest.
        Returns a patch ("time"+"hero") — merge HP/resource recovery into the same "hero". Do NOT
        call advance_round afterwards — this tool has already reset the action economy.
        """)]
    public string LongRest() => Rest(isLong: true);

    [McpServerTool]
    [Description("""
        Short Rest (call only if you've already decided it's allowed): partOfDay+1, day+1 when
        passing through night, totalRounds→1, resets actions to maxValue and speedLeft to speedMax,
        removes effects with expiresAtRound (untilLongRest ones are kept). Returns a patch
        ("time"+"hero") — merge resource recovery into the same "hero". Do NOT call
        advance_round.
        """)]
    public string ShortRest() => Rest(isLong: false);

    private string Rest(bool isLong)
    {
        string kind = isLong ? "long_rest" : "short_rest";
        Log($"{kind} →");
        try
        {
            var ws = LoadWorldState();
            var hero = ws.Hero ?? throw new InvalidOperationException("hero missing");

            int steps = isLong ? 2 : 1;
            int currentIndex = Array.IndexOf(_partsOfDay, PartsOfDay.Normalize(ws.Time.PartOfDay));
            if (currentIndex < 0) currentIndex = 0;
            int newIndex = (currentIndex + steps) % _partsOfDay.Length;
            int dayIncrement = (currentIndex + steps) / _partsOfDay.Length;

            var effects = hero.Effects ?? [];
            var deletions = new List<object>();
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                if (e.Deleted == true) continue;
                bool remove = e.ExpiresAtRound.HasValue || (isLong && e.UntilLongRest == true);
                if (remove) deletions.Add(new { id = i, deleted = true });
            }

            // A rest is effectively a new round for the hero: reset actions/speed in the same
            // patch so the AI doesn't have to call advance_round separately (and risk overwriting
            // totalRounds:1, computed right here, with a second, inconsistent value).
            var actionPatches = new List<object>();
            var actions = hero.Actions ?? [];
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Deleted == true) continue;
                actionPatches.Add(new { id = i, value = actions[i].MaxValue });
            }

            var heroPatch = new Dictionary<string, object?>
            {
                ["actions"] = actionPatches,
                ["speedLeft"] = hero.SpeedMax,
            };
            if (deletions.Count > 0) heroPatch["effects"] = deletions;

            var result = new Dictionary<string, object?>
            {
                ["time"] = new { totalRounds = 1, partOfDay = _partsOfDay[newIndex], day = ws.Time.Day + dayIncrement },
                ["hero"] = heroPatch,
            };

            string resultJson = JsonSerializer.Serialize(result, _writeOptions);
            Log($"{kind} ← ({resultJson.Length} chars): {resultJson}");
            return resultJson;
        }
        catch (Exception ex)
        {
            Log($"{kind} ← (ERROR) {ex.Message}");
            return Error(ex.Message);
        }
    }

    [McpServerTool]
    [Description("""
        Door action: open/close/break (the hero must stand exactly on From or To) or
        hide/reveal (no position requirement). Returns {"success":true,"patch":{...}} to
        merge, or {"success":false,"reason":"..."} — describe the refusal, don't resolve it yourself.
        """)]
    public string DoorAction(
        [Description("door id from map.doors")] int doorId,
        [Description("action variant: open|close|break|hide|reveal")] string action)
    {
        string logName = $"door_action({action})";
        Log($"{logName} → id:{doorId}");
        try
        {
            var ws = LoadWorldState();
            var doors = ws.Map.Doors ?? throw new InvalidOperationException("map.doors missing");
            if (doorId < 0 || doorId >= doors.Count)
                throw new ArgumentException($"door id {doorId} out of range (0..{doors.Count - 1})");
            var door = doors[doorId];

            (object fields, bool requiresAdjacency) = action.ToLowerInvariant() switch
            {
                "open"   => ((object)new { id = doorId, isDoorOpen = true }, true),
                "close"  => (new { id = doorId, isDoorOpen = false }, true),
                "break"  => (new { id = doorId, isDoor = false, color = (object?)null }, true),
                "hide"   => (new { id = doorId, hidden = true }, false),
                "reveal" => (new { id = doorId, hidden = false }, false),
                _ => throw new ArgumentException($"unknown action '{action}' — expected open/close/break/hide/reveal"),
            };

            if (requiresAdjacency)
            {
                bool isPhysicalDoor = door.IsDoor ?? (door.Color?.Count == 3);
                if (!isPhysicalDoor)
                    return Fail(logName, $"map.doors id:{doorId} is a passage, not a door; the action isn't available.");

                var hero = ws.Hero ?? throw new InvalidOperationException("hero missing");
                var pos = hero.Position ?? throw new InvalidOperationException("hero.position missing");
                // A door is the boundary BETWEEN From and To, not a separate cell: the hero must stand
                // exactly on one of them, not next to it (Chebyshev ≤1 to From/To would allow the cell
                // BEYOND the door — the hero "one cell away" from it, not right up against it).
                int dist = Math.Min(ChebyshevDistance(pos, door.From), ChebyshevDistance(pos, door.To));
                if (dist > 0)
                    return Fail(logName,
                        $"Door id:{doorId} is too far (hero at [{pos[0]},{pos[1]}], door between " +
                        $"[{door.From[0]},{door.From[1]}] and [{door.To[0]},{door.To[1]}]) — step right up to it (onto From or To).");
            }

            var result = new { success = true, patch = new { map = new { doors = new[] { fields } } } };
            string resultJson = JsonSerializer.Serialize(result, _writeOptions);
            Log($"{logName} ← ({resultJson.Length} chars): {resultJson}");
            return resultJson;
        }
        catch (Exception ex)
        {
            Log($"{logName} ← (ERROR) {ex.Message}");
            return Error(ex.Message);
        }
    }

    private string Fail(string action, string reason)
    {
        string resultJson = JsonSerializer.Serialize(new { success = false, reason }, _writeOptions);
        Log($"{action} ← (refused) {reason}");
        return resultJson;
    }

    private static int ChebyshevDistance(List<int> a, List<int> b) =>
        Math.Max(Math.Abs(a[0] - b[0]), Math.Abs(a[1] - b[1]));

    // ── IO helpers (see CalculateMovementTools for the same pattern) ─────────

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
            File.AppendAllText(config.LogPath, $"  MCP {message}\n", new UTF8Encoding(false));
    }
}

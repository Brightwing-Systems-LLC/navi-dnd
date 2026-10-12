using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using NaviDnD;

namespace NaviDnD.McpServer;

[McpServerToolType]
public sealed class UseReactionTools(McpServerConfig config)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object _logLock = new();

    private string AskPlayerRequestPath =>
        Path.Combine(Path.GetDirectoryName(config.WorldStatePath)!, "ask_player_request.json");

    [McpServerTool]
    [Description("""
        D&D 5e reaction check: the hero has an unused Reaction and an opportunity to use it
        has just come up (an Opportunity Attack, the Shield spell and similar reaction
        triggers) — ask whether they want to use it and wait for the answer (up to 50 seconds).
        Use ONLY for this. NEVER ask "what do you do?" — the player writes their
        next action themselves. Only a real question about the specific situation; not
        a placeholder, not test text. No real reason to ask — don't call this tool at all.
        Fields:
        - history[]: entries shown to the player before the question. Each: text (1-2 sentences, mechanics in [...]), text may be SSML <speak> for voice-over; optional author, patch. The last entry = the question itself.
        - options: 2-4 answer options, each at most 2 words. More than 4 options or longer ones — omit entirely (free text input).
        Returns { "answer": "..." } or { "answer": null, "timedOut": true } if the player didn't answer within 50 seconds.
        """)]
    public string UseReaction(
        [Description("Dialog entries before the question. Each has text; text may be SSML <speak> for voice-over; optional author, patch.")] RequestHistoryEntry[] history,
        [Description("Answer options for the player. Omit for free text input.")] string[]? options)
    {
        // Real log: the tool was called with an empty history/question — the dialog would still have opened
        // for the player and waited 50s for an answer to nothing. Reject immediately without opening the UI.
        if (history is not { Length: > 0 } || string.IsNullOrWhiteSpace(history[^1].Text))
        {
            Log("use_reaction → (REJECTED) empty history or last entry has no question text");
            return JsonSerializer.Serialize(new
            {
                error = "history must be non-empty and its last entry's text must be a real question. " +
                        "Do not call use_reaction without an actual question for the player."
            }, _json);
        }

        var request = new AskPlayerRequest
        {
            History = history?.ToList() ?? [],
            Options = options,
        };

        string lastText = history is { Length: > 0 } ? history[^1].Text : "";
        string optDesc = options is { Length: > 0 } ? string.Join(" | ", options) : "free-text";
        Log($"use_reaction → \"{lastText}\" [{optDesc}]");

        WriteRequest(request);

        var deadline = DateTime.UtcNow.AddSeconds(50);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            var current = TryReadRequest();
            if (current?.Answered == true)
            {
                TryDelete();
                Log($"use_reaction ← answer=\"{current.Answer}\"");
                return JsonSerializer.Serialize(new { answer = current.Answer }, _json);
            }
        }

        TryDelete();
        Log("use_reaction ← timedOut");
        return JsonSerializer.Serialize(new { answer = (string?)null, timedOut = true }, _json);
    }

    private void WriteRequest(AskPlayerRequest request)
    {
        string json = JsonSerializer.Serialize(request, _json);
        RetryOnIo(() => File.WriteAllText(AskPlayerRequestPath, json, new UTF8Encoding(false)));
    }

    private AskPlayerRequest? TryReadRequest()
    {
        try
        {
            if (!File.Exists(AskPlayerRequestPath)) return null;
            string json = RetryOnIo(() =>
            {
                using var stream = new FileStream(AskPlayerRequestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            });
            return JsonSerializer.Deserialize<AskPlayerRequest>(json, _json);
        }
        catch { return null; }
    }

    private void TryDelete()
    {
        try { if (File.Exists(AskPlayerRequestPath)) File.Delete(AskPlayerRequestPath); }
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
            File.AppendAllText(config.LogPath, $"  MCP {message}\n", new System.Text.UTF8Encoding(false));
    }
}

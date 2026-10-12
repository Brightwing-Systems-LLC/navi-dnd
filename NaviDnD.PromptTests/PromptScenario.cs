using System.Text.Json;
using System.Text.Json.Nodes;
using NaviDnD.Clients;

namespace NaviDnD.PromptTests;

// Calls the REAL Claude (ClaudeCliAiProvider) with the game's actual system prompt
// (NaviDnD/Prompts/{ruleSet}/{action}/systemPrompt.md) on a real WorldState scenario — this is
// what "testing the system prompt" means: we check that for the given scenario the prompt produces a response
// the engine can actually apply (Storage.ApplyUpdateWorldState does not throw) AND that
// contains the changes expected for the scenario (see the explicit asserts in the individual tests). With
// NAVIDND_RECORD_PROMPTS=1 it records the "live" response as the canonical mock for NaviDnD.UiTests — so
// the UI tests stay fast and deterministic, but their mock comes from a real run of the
// prompt rather than being written by hand.
//
// IMPORTANT (NaviDnD.PromptTests is a separate project — NOT part of the solution-wide test run):
// - Every call is a real request to Claude: costly and slow (seconds per call).
// - The response is nondeterministic — check STRUCTURE and key INVARIANTS (e.g. "HP went down",
//   "object removed"), not the exact narrative text.
// - AppConfig.ProjectRoot (used by Storage.SavePath/DraftSavePath) resolves RELATIVE TO THE CURRENT
//   assembly — for this project that is NaviDnD.PromptTests/Storage/, not the production NaviDnD/Storage/,
//   so these tests never touch the user's real save.
// - Before committing, read the recorded response.json with your own eyes rather than trusting it blindly —
//   the model's nondeterministic output can occasionally be wrong in meaning (not in JSON).
public static class PromptScenario
{
    private static readonly string RealNaviDndDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NaviDnD"));

    private static readonly string UiTestsFixturesDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NaviDnD.UiTests", "Fixtures"));

    // Path to this project's sources (not bin/Debug/...) — no copy-to-output setup needed
    // for Fixtures/, the same trick UiTestsFixturesDir uses for the sibling project.
    private static readonly string LocalFixturesDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string ReadSystemPrompt(string ruleSet, string action) =>
        File.ReadAllText(Path.Combine(RealNaviDndDir, "Prompts", ruleSet, action, "systemPrompt.md"));

    // Loads a ready-made scenario from a NaviDnD.UiTests fixture (the same files the E2E UI
    // tests use) — so the prompt-test scenario and the UI-test scenario are guaranteed to match.
    public static Storage LoadUiTestFixture(string fixtureFileName)
    {
        var storage = new Storage();
        storage.LoadFrom(Path.Combine(UiTestsFixturesDir, fixtureFileName));
        return storage;
    }

    // Scenarios specific to prompt testing (not needed by NaviDnD.UiTests) — e.g. several
    // identical enemies to check target ambiguity (ask_player).
    public static Storage LoadLocalFixture(string fixtureFileName)
    {
        var storage = new Storage();
        storage.LoadFrom(Path.Combine(LocalFixturesDir, fixtureFileName));
        return storage;
    }

    // draftActions — the same as ClaudeCliAiProvider._draftActions (CreateNewGame/FixHeroForNewGame/
    // StartNewGame read/write the draft via MCP, not the production file).
    private static readonly HashSet<string> DraftActions = ["CreateNewGame", "FixHeroForNewGame", "StartNewGame"];

    // Model under test: NAVIDND_PROMPT_PROVIDER=codex — Codex CLI (NAVIDND_CODEX_MODEL — model, NAVIDND_CODEX_PATH —
    // path), otherwise Claude CLI. Same prompts and MCP server — this shows where a different model deviates from the format.
    public static AppConfig TestConfig()
    {
        var config = new AppConfig();
        if (Environment.GetEnvironmentVariable("NAVIDND_PROMPT_PROVIDER") is { Length: > 0 } p) config.AiProvider = p.Trim().ToLowerInvariant();
        if (Environment.GetEnvironmentVariable("NAVIDND_CODEX_MODEL") is { Length: > 0 } m) config.CodexModel = m.Trim();
        if (Environment.GetEnvironmentVariable("NAVIDND_CODEX_PATH") is { Length: > 0 } c) config.CodexCliPath = c.Trim();
        if (Environment.GetEnvironmentVariable("NAVIDND_CODEX_REASONING") is { } effort && effort is "low" or "medium" or "high")
            config.CodexReasoningEffort = effort;
        return config;
    }

    // Saves the scenario to the path the MCP server will read (get_world_state) and calls the
    // REAL provider. actionPath is passed to select allowedTools/the MCP config (see
    // ClaudeCliAiProvider.PrepareWorkDir) — only the name of the last segment (=action) matters.
    public static async Task<string> CallRealAiAsync(
        string ruleSet, string action, Storage storage, string userMessage, string? model = null)
    {
        if (DraftActions.Contains(action)) storage.SaveDraft(); else storage.Save();

        var config = TestConfig();
        IAiProvider provider = new SwitchableAiProvider(config, new AiLogger());
        string actionPath = Path.Combine(RealNaviDndDir, "Prompts", ruleSet, action);
        string promptText = ReadSystemPrompt(ruleSet, action);

        return await provider.Complete([promptText], userMessage, actionPath, model ?? config.ClaudeModel);
    }

    // Like CallRealAiAsync, but also spins up a background "auto-responder" for roll_dice/ask_player
    // (the same roll_request.json/ask_player_request.json files that GameAiClient.SpinAsync polls in the real
    // game — here without UI, we answer immediately with a placeholder). Needed for scenarios
    // where the prompt MUST call one of these MCP tools (attack → roll, ambiguous target →
    // question) — without the auto-responder the call would hang until the real MCP tool timeout (50s).
    public static async Task<AutoAnswerResult> CallRealAiWithAutoAnswersAsync(
        string ruleSet, string action, Storage storage, string userMessage,
        int rollValue = 15, string? askPlayerAnswer = null, string? model = null)
    {
        if (DraftActions.Contains(action)) storage.SaveDraft(); else storage.Save();

        string storageDir = Path.GetDirectoryName(Storage.SavePath)!;
        string rollPath = Path.Combine(storageDir, "roll_request.json");
        string askPath = Path.Combine(storageDir, "ask_player_request.json");
        string selectPath = Path.Combine(storageDir, "select_target_request.json");
        // In case of leftovers from a previous failed run — it's our own, non-production folder, but better not to guess.
        TryDelete(rollPath);
        TryDelete(askPath);
        TryDelete(selectPath);

        var tracker = new AutoAnswerResult("", false, false);
        var trackerBox = new AutoAnswerBox(tracker);

        using var cts = new CancellationTokenSource();
        var pollTask = AutoAnswerLoopAsync(rollPath, askPath, selectPath, storage, rollValue, askPlayerAnswer, trackerBox, cts.Token);
        try
        {
            string response = await CallRealAiAsync(ruleSet, action, storage, userMessage, model);
            return trackerBox.Value with { RawResponse = response };
        }
        finally
        {
            cts.Cancel();
            try { await pollTask; } catch (OperationCanceledException) { }
            TryDelete(rollPath);
            TryDelete(askPath);
            TryDelete(selectPath);
        }
    }

    private sealed class AutoAnswerBox(AutoAnswerResult initial)
    {
        public AutoAnswerResult Value = initial;
    }

    private static async Task AutoAnswerLoopAsync(
        string rollPath, string askPath, string selectPath, Storage storage,
        int rollValue, string? askPlayerAnswer, AutoAnswerBox box, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!box.Value.RollDiceTriggered && File.Exists(rollPath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(rollPath, ct);
                        var req = JsonSerializer.Deserialize<RollRequest>(json, JsonOptions);
                        if (req != null && !req.Answered)
                        {
                            req.Answered = true;
                            req.Roll1 = rollValue;
                            req.Answer = rollValue;
                            req.Critical = rollValue == 20 ? "critical_success" : rollValue == 1 ? "critical_failure" : null;
                            await File.WriteAllTextAsync(rollPath, JsonSerializer.Serialize(req, JsonOptions), ct);
                            box.Value = box.Value with { RollDiceTriggered = true };
                        }
                    }
                    catch { /* the MCP server may still have been writing the file — try again on the next tick */ }
                }

                if (!box.Value.AskPlayerTriggered && File.Exists(askPath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(askPath, ct);
                        var req = JsonSerializer.Deserialize<AskPlayerRequest>(json, JsonOptions);
                        if (req != null && !req.Answered)
                        {
                            req.Answered = true;
                            req.Answer = askPlayerAnswer ?? (req.Options is { Length: > 0 } ? req.Options[0] : "I continue");
                            await File.WriteAllTextAsync(askPath, JsonSerializer.Serialize(req, JsonOptions), ct);
                            box.Value = box.Value with { AskPlayerTriggered = true };
                        }
                    }
                    catch { }
                }

                // select_target: pick the first valid cell not occupied by the hero (no UI — treat everything as visible).
                if (!box.Value.SelectTargetTriggered && File.Exists(selectPath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(selectPath, ct);
                        var req = JsonSerializer.Deserialize<TargetSelectionRequest>(json, JsonOptions);
                        if (req != null && !req.Answered)
                        {
                            var ws = storage.WorldState;
                            var heroPos = ws.Hero?.Position;
                            var cell = TargetGeometry.SelectableCells(ws, req, (_, _) => true, (_, _) => true)
                                .Where(c => heroPos is not { Count: >= 2 } || c != (heroPos[0], heroPos[1]))
                                .OrderBy(c => c.col).ThenBy(c => c.row)
                                .Cast<(int col, int row)?>().FirstOrDefault();
                            req.Answered = true;
                            req.Result = cell is { } p
                                ? new TargetSelectionResult { Targets = [TargetGeometry.Describe(ws, p.col, p.row)] }
                                : new TargetSelectionResult { Cancelled = true };
                            await File.WriteAllTextAsync(selectPath, JsonSerializer.Serialize(req, JsonOptions), ct);
                            box.Value = box.Value with { SelectTargetTriggered = true };
                        }
                    }
                    catch { }
                }

                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // Structural check + parsing for further asserts: applies the response to the SAME scenario Storage
    // (as the real engine would) — both the root patch and the nested history[].patch entries
    // (see Prompts/.../systemPrompt.md § COMBAT). Throws if the AI returned an invalid/
    // inapplicable diff. Returns the texts of the history entries — handy for checking that the mechanics
    // actually ended up in "[...]", as the prompt requires.
    public static PromptResult ApplyAndParse(Storage storage, string response)
    {
        string json = ExtractJson(response);
        var root = ParseMergingDuplicateRootKeys(json)
            ?? throw new InvalidOperationException($"The AI response did not parse as a JSON object:\n{response}");

        var historyNode = root["history"]?.DeepClone();
        root.Remove("history");
        string rootJson = root.ToJsonString();
        if (rootJson != "{}") storage.ApplyUpdateWorldState(rootJson);

        var texts = new List<string>();
        if (historyNode is JsonArray historyArr)
        {
            foreach (var entryNode in historyArr)
            {
                if (entryNode is not JsonObject entry) continue;
                if (entry["text"]?.GetValue<string>() is { Length: > 0 } text) texts.Add(text);
                if (entry["patch"] is JsonObject patch)
                {
                    // Like SequentialHistoryPlayer.AnimateAndApplyPatch in the real engine: movePath is
                    // an internal field used only for animation, "position" (the last element) already arrived
                    // separately — strip it before ApplyUpdateWorldState, otherwise Storage.ValidateKeys
                    // logs "Unknown field 'movePath'" (not an error, but extra noise that the real
                    // engine never produces with the same scheme).
                    if (patch["map"]?["entities"] is JsonArray entities)
                        foreach (var entityNode in entities.OfType<JsonObject>())
                            entityNode.Remove("movePath");
                    storage.ApplyUpdateWorldState(patch.ToJsonString());
                }
            }
        }

        return new PromptResult(response, texts, root);
    }

    public static void RecordAsMock(string response, string mockResponsesFolder, string action)
    {
        if (Environment.GetEnvironmentVariable("NAVIDND_RECORD_PROMPTS") != "1") return;
        string targetDir = Path.Combine(UiTestsFixturesDir, "MockResponses", mockResponsesFolder, action);
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "response.json"), response);
    }

    // Copy of SequentialHistoryPlayer.ParseMergingDuplicateRootKeys (private there; the real engine
    // tolerates duplicate root keys such as two "hero": entries instead of one merged one — see its
    // comment) — the test must likewise not fail on this model slip, rather than reject a response
    // that would apply just fine in the real game.
    private static JsonObject? ParseMergingDuplicateRootKeys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        var merged = new JsonObject();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var value = JsonNode.Parse(prop.Value.GetRawText());
            if (merged.TryGetPropertyValue(prop.Name, out var existingNode) &&
                existingNode is JsonObject existing && value is JsonObject incoming)
            {
                foreach (var kv in incoming.ToList())
                    existing[kv.Key] = kv.Value?.DeepClone();
            }
            else
            {
                merged[prop.Name] = value;
            }
        }
        return merged;
    }

    // Copy of GameAiClient.ExtractJson (private there) — cuts the first balanced {...} out of
    // the model's response text (in case of surrounding text/markdown, which ClaudeCliAiProvider
    // usually strips itself, but extra protection here doesn't hurt).
    private static string ExtractJson(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return text;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return text[start..(i + 1)]; }
        }

        return text[start..] + new string('}', depth);
    }
}

// Texts of all history entries in the response (for checking narrative/mechanics) + the raw root JsonObject
// (for targeted checks of non-standard fields, if needed).
public record PromptResult(string RawResponse, List<string> HistoryTexts, JsonObject Root)
{
    public bool AnyTextContains(string substring) =>
        HistoryTexts.Any(t => t.Contains(substring, StringComparison.OrdinalIgnoreCase));
}

public record AutoAnswerResult(string RawResponse, bool RollDiceTriggered, bool AskPlayerTriggered, bool SelectTargetTriggered = false);

using NaviDnD.Data.Models;
using NaviDnD.Helpers;
using NaviDnD.MapGen.Generators;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace NaviDnD.Clients;

public class GameAiClient
{
    private readonly WorldState _settings;
    private readonly Storage _storage;
    private readonly IAiProvider _provider;
    private readonly string _ruleSet;
    private readonly AppConfig _appConfig;
    public DialogDisplay? ActiveDialog { get; set; }

    private static readonly string _rollRequestPath =
        Path.Combine(AppConfig.ProjectRoot, "Storage", "roll_request.json");

    private static readonly string _askPlayerRequestPath =
        Path.Combine(AppConfig.ProjectRoot, "Storage", "ask_player_request.json");

    private static readonly string _selectTargetRequestPath =
        Path.Combine(AppConfig.ProjectRoot, "Storage", "select_target_request.json");

    private static readonly JsonSerializerOptions _rollJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly JsonSerializerOptions _serializeOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public GameAiClient(WorldState settings, Storage storage, IAiProvider provider, AppConfig config)
    {
        _settings = settings;
        _storage = storage;
        _provider = provider;
        _ruleSet = config.RuleSet;
        _appConfig = config;
    }

    // Game rules (the folder of prompts and rules data — Prompts/{RuleSet}).
    public string RuleSet => _ruleSet;

    public async Task CreateNewGame(NewGameData newGameData)
    {
        _storage.ResetWorldState();
        // The MCP server needs an existing state file at startup (see Program.cs) — previously
        // CreateNewGame ran with no MCP at all (allowedTools:null); now it needs find_icon.
        _storage.SaveDraft();

        string input = JsonSerializer.Serialize(newGameData, _serializeOptions);
        string userMessage = $"The player's wishes for their character and the rules:\n{input}";
        await CallAction(userMessage, nameof(CreateNewGame), _appConfig.ClaudeCreateNewGameModel);

        // What the player chose in the questionnaire — as is, on top of the model's answer.
        if (_settings.Hero is { } hero)
        {
            if (int.TryParse(newGameData.Level, out int level) && level > 0)
            {
                hero.Level = level;
                hero.Xp = LevelRules.Current.XpFor(level);
            }
            hero.VisionFt = null;   // hero's vision comes from light (engine); the model used to set "Vision 5 ft"
            if (!string.IsNullOrWhiteSpace(newGameData.Image)) hero.Image = newGameData.Image;
            if (newGameData.Color is { Count: 3 }) hero.Color = [.. newGameData.Color];
            if (!string.IsNullOrWhiteSpace(newGameData.Symbol)) hero.Symbol = newGameData.Symbol.Trim();
        }
    }

    // New-game questionnaire: the model picks hero portraits by name and description (find_icon) — they go
    // at the start of the gallery. Error/empty answer — empty list (the gallery keeps the stock set).
    // New game with a hero from the library: the hero as they were (level, stats, skills, spells, abilities), personality —
    // from the questionnaire; the code resets last game's state (full HP, effects, inspiration, position), while gear and
    // money for the new adventure are handed out by the model in one short call (FixHeroForNewGame).
    public async Task ReuseHero(NewGameData data, string heroJson)
    {
        _storage.ResetWorldState();
        _storage.ApplySavedState("{\"hero\":" + heroJson + "}");   // level and XP — as they were
        if (_settings.Hero is not { } hero) return;
        if (hero.Hp is { } hp && hp.Split('/') is [_, var max] && max.Trim().Length > 0) hero.Hp = $"{max.Trim()}/{max.Trim()}";
        hero.Effects = [];
        hero.Inspiration = null;
        hero.Stealth = null;
        hero.Position = null;
        hero.VisionFt = null;   // hero's vision comes from light (engine)
        if (!string.IsNullOrWhiteSpace(data.Name)) hero.Name = data.Name.Trim();
        if (!string.IsNullOrWhiteSpace(data.Symbol)) hero.Symbol = data.Symbol.Trim();
        if (!string.IsNullOrWhiteSpace(data.Image)) hero.Image = data.Image;
        if (data.Color is { Count: 3 }) hero.Color = [.. data.Color];
        await FixHeroForNewGame(
            "This hero from a previous game is starting a new adventure. Do NOT change level, ability scores, skills, spells, " +
            "abilities, race or class. Replace gear and money with the usual starting kit for their class and " +
            "level (remove unique/plot items from the previous game); resources and spell slots — full." +
            (string.IsNullOrWhiteSpace(data.Description) ? "" : $" The player refined the hero's description: {data.Description}"));
    }

    // Hero questionnaire: a description (appearance, personality, backstory — not the character sheet) — invent one from name/race/class
    // or revise the current one per the player's request. null — it didn't work.
    public async Task<string?> DescribeHero(string name, string race, string cls, string? current, string? request, string? extra = null)
    {
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(DescribeHero));
        try
        {
            string userMessage = $"Name: {name}\nRace: {race}\nClass: {cls}" + (string.IsNullOrWhiteSpace(extra) ? "" : $"\n{extra.Trim()}")
                                 + (string.IsNullOrWhiteSpace(current) ? "" : $"\nCurrent description (from the player):\n{current.Trim()}")
                                 + (string.IsNullOrWhiteSpace(request) ? "" : $"\nPlayer request: {request.Trim()}");
            string response = await CompleteConfigured([ReadPrompt(actionPath)], userMessage, actionPath, _appConfig.ClaudeCreateNewGameModel);
            string? text = JsonNode.Parse(ExtractJson(response))?["description"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (AiSetupException) { throw; }
        catch
        {
            return null;
        }
    }

    // "Level Up" window: what the class gains at the level and the choice groups — JSON for LevelUp.LoadPlan (parsed by the window,
    // on its own thread). null — it didn't work.
    public async Task<string?> LevelUpOptions(LevelUp levelUp)
    {
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(LevelUpOptions));
        string response = "";
        try
        {
            response = await CompleteConfigured([ReadPrompt(actionPath)], levelUp.OptionsRequest(), actionPath, _appConfig.ClaudeModel);
            return await EnsureValidJson(ExtractJson(response));
        }
        catch (AiSetupException) { throw; }
        catch (Exception ex)
        {
            Logger?.LogNote($"LevelUpOptions: {ex}\nResponse: {response}");
            return null;
        }
    }

    // The player's request for a custom level-up option (input field) — same options prompt; JSON for LevelUp.MergeCustom.
    public async Task<string?> LevelUpCustom(LevelUp levelUp, string request)
    {
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(LevelUpOptions));
        string response = "";
        try
        {
            response = await CompleteConfigured([ReadPrompt(actionPath)], levelUp.CustomRequest(request), actionPath, _appConfig.ClaudeModel);
            return await EnsureValidJson(ExtractJson(response));
        }
        catch (AiSetupException) { throw; }
        catch (Exception ex)
        {
            Logger?.LogNote($"LevelUpCustom: {ex}\nResponse: {response}");
            return null;
        }
    }

    // Choice made: a new hero (copy of the current one) with what was gained and chosen; null — the DM didn't respond. Nothing
    // to write to the sheet (no features, no choices, no new numbers) — no model call.
    public async Task<Hero?> LevelUpApply(LevelUp levelUp)
    {
        if (!levelUp.NeedsMaster) return levelUp.Apply(null);
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(LevelUpApply));
        string response = "";
        try
        {
            response = await CompleteConfigured([ReadPrompt(actionPath)], levelUp.ApplyRequest(), actionPath, _appConfig.ClaudeModel);
            return levelUp.Apply(await EnsureValidJson(ExtractJson(response)));
        }
        catch (AiSetupException) { throw; }
        catch (Exception ex)
        {
            Logger?.LogNote($"LevelUpApply: {ex}\nResponse: {response}");
            return null;
        }
    }

    public async Task FixHeroForNewGame(string request, DialogDisplay? dialog = null)
    {
        _storage.SaveDraft();
        var ctx = new AiContextBuilder(_settings);
        string userMessage = ctx.HeroMinimalState(request);
        if (dialog == null)
        {
            await CallAction(userMessage, nameof(FixHeroForNewGame), _appConfig.ClaudeModel);
            return;
        }

        // Like SendAction: history is typed out character by character (with sound) via SequentialHistoryPlayer,
        // rather than appearing all at once through a single ApplyUpdateWorldState as in CallAction.
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(FixHeroForNewGame));
        string response = "";
        try
        {
            response = await CompleteWithSpinner([ReadPrompt(actionPath)], userMessage, actionPath, _appConfig.ClaudeModel, dialog.TickSpinner);
            await PlayResponse(ExtractJson(response), dialog.AppendStreamChunk, dialog.NewStreamingMessage, onRedraw: null);
        }
        catch (Exception ex)
        {
            RecordError(ex, response);
        }
    }

    // New-game questionnaire, "World" step: the concept of a new world (name, premise, kingdoms with capitals) — into its
    // chronicle, so the player sees the world before starting. Creates no places — the DM adds them during play.
    // description — the current description (written/edited by the player): the model revises it per the request instead of rewriting it.
    public async Task<bool> CreateWorld(WorldMap world, string? wish, string? name = null, string? description = null)
    {
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(CreateWorld));
        L.SetWorld(world.Language);   // the world description is in the world's language
        try
        {
            string userMessage = WorldAtlas.TerrainSummary(world)
                                 + (string.IsNullOrWhiteSpace(name) ? "" : $"\nThe world name was set by the player: \"{name.Trim()}\" — keep it (name).")
                                 + (string.IsNullOrWhiteSpace(description) ? "" : $"\nCurrent world description (from the player) — revise per the request, keep what they did not ask to change:\n{description.Trim()}")
                                 + (string.IsNullOrWhiteSpace(wish) ? "" : $"\nPlayer request: {wish}");
            string response = await CompleteConfigured([ReadPrompt(actionPath)], userMessage, actionPath, _appConfig.ClaudeStartNewGameModel);
            using var doc = JsonDocument.Parse(ExtractJson(response));
            bool ok = GameWorld.ApplyChronicle(world, doc.RootElement);
            if (ok && !string.IsNullOrWhiteSpace(name)) world.Chronicle.Name = name.Trim();
            return ok;
        }
        catch (AiSetupException) { throw; }
        catch
        {
            return false;
        }
    }

    public async Task StartNewGame(string _)
    {
        // The model plans the location for the story with the plan_location tool (how many blocks, where, what's in each);
        // the code builds the geometry from the plan (LocationGrower.CreatePlanned → ChunkGenerator), the tool returns
        // the starting block — the model populates it in the same response, the rest via PopulateChunk on approach.
        _settings.Map.Chunks = null;

        // Game world: the code only provided the land (terrain, kingdoms, roads); the world and kingdom concept, the starting
        // place and places known to the hero are set by the model in the same response (world, add_place).
        // World from the new-game wizard (chosen or created at the "World" step; the chronicle continues).
        if (_storage.NewGameData?.WorldId is { } wid) WorldLibrary.SetActive(wid);
        // Who the hero is (the first sentence of the questionnaire description: "a wiry drow woman…") — into every turn's context (hero.aiInfo):
        // without it the DM addressed a heroine as male. The full description is not saved.
        if (_settings.Hero is { AiInfo: null or { Count: 0 } } h && _storage.NewGameData?.Description is { Length: > 0 } desc)
        {
            string first = desc.Trim();
            int dot = first.IndexOfAny(['.', '!', '?']);
            if (dot > 0) first = first[..(dot + 1)];
            h.AiInfo = [first.Length > 220 ? first[..220] + "…" : first];
        }
        var world = WorldLibrary.Current;
        _settings.World = new GameWorldLink { Id = world.Id };
        // The game is in the world's language (narration, history, names); it is also in every request (lang:).
        _settings.Language = world.Language;
        L.SetWorld(world.Language);
        // The world is already described in past games (chronicle) — same concept and places; a new one — only the land.
        // Kingdoms already described (past games or "INVENT" in the questionnaire) — don't change the concept; only a
        // name/description from the player — take them and invent the kingdoms.
        bool known = world.Chronicle.Kingdoms.Count > 0;
        string playerConcept = world.Chronicle.Name is { Length: > 0 } || world.Chronicle.Description is { Length: > 0 }
            ? $"\nThe world was set by the player — name \"{world.Chronicle.Name ?? world.Name}\"{(world.Chronicle.Description is { Length: > 0 } pd ? $", description: {pd}" : "")}. Use them (do not change name/description); invent kingdoms in the spirit of the description."
            : "";
        string worldBrief = known
            ? "\n\nThe world is already described — do NOT change the concept, kingdoms or places; anything new — add_place. Chronicle:\n"
              + WorldAtlas.Overview(GameWorld.Compose(world, _settings.World, includeHidden: true), withDmNotes: true)
              + "\nLand:\n" + WorldAtlas.TerrainSummary(world)
            : "\n\nThe world's land (concept — in world, places — add_place):\n" + WorldAtlas.TerrainSummary(world) + playerConcept;
        // Adventure parameters ("Adventure" step): genre, focus, length, difficulty, pace.
        if (_storage.NewGameData?.AdventureStyle is { Length: > 0 } style)
            worldBrief = $"\nAdventure parameters: {style}" + worldBrief;
        worldBrief += $"\nNow: day {_settings.Time.Day}, {PartsOfDay.Normalize(_settings.Time.PartOfDay)} — describe the scene at this time of day or set another one in time.";
        // Starting place chosen by the player on the map ("Adventure" step).
        if (_storage.NewGameData?.StartPlace is { Length: > 0 } sp)
            worldBrief += $"\nThe player chose the starting place: \"{sp}\" — start there (world.place).";
        else if (_storage.NewGameData is { StartX: int sx, StartY: int sy })
            worldBrief += $"\nThe player chose a start on the map: cell [{sx},{sy}] — {WorldBiomes.Get(world.BiomeAt(sx, sy)).Name.ToLowerInvariant()}, {WorldAtlas.Surroundings(world, sx, sy, named: true)}; starting place — add_place nearby (near is not needed: give kingdom and terrain for this cell) or exactly there.";

        // Save the hero and map to the draft file before running the AI — the MCP tools read this file.
        // The live worldState.json is left untouched until the world is created successfully.
        _storage.SaveDraft();

        string userMessage;
        if (_ruleSet == "Dnd5e")
        {
            userMessage = $"Hero data: symbol: {_settings.Hero?.Symbol}, name: {_settings.Hero?.Name}, passive Perception: {GetPassivePerception()}" +
                          $"\n\nWishes for the game: {_storage.NewGameData?.SettingWish}" + worldBrief;
        }
        else
        {
            userMessage = $"Hero data: symbol: {_settings.Hero?.Symbol}, name: {_settings.Hero?.Name}" +
                          $"\n\nWishes for the game: {_storage.NewGameData?.SettingWish}" + worldBrief;
        }

        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(StartNewGame));
        string systemPrompt = ReadPrompt(actionPath);
        string response = "";
        try
        {
            response = await CompleteConfigured([systemPrompt], userMessage, actionPath, _appConfig.ClaudeStartNewGameModel);

            // The MCP tools may have written to the draft — reload it so nothing is lost.
            _storage.LoadDraft();
            // The plan_location flag is only needed mid-game — here the map is already taken from the draft.
            try { File.Delete(Path.Combine(Path.GetDirectoryName(Storage.DraftSavePath) ?? ".", Storage.LocationPlannedFlag)); } catch { }

            string json = ExtractJson(response);
            // Old-format response (a full map with cols/rows/rooms — UI test mocks): applied as
            // before, on top of an empty map, without block generation.
            if (JsonNode.Parse(json)?["map"]?["cols"] != null)
            {
                var map = _settings.Map;
                map.Rooms = []; map.Doors = []; map.Area = []; map.Entities = []; map.Objects = [];
                map.Chunks = null;
            }
            // plan_location was not called — fallback: a single block so there is somewhere to play.
            else if (_settings.Map.Chunks is not { Count: > 0 })
                LocationGrower.CreateStart(_settings);
            if (_settings.Map.Chunks?.FirstOrDefault(c => c.Exits.Any(e => e.External)) is { } startChunk)
                startChunk.Populated = true;
            // The DM's intro (history) is not shown right away: the game screen types it out character by character, with sound
            // (PlayIntro — like a normal DM response); everything else goes into the state immediately.
            if (JsonNode.Parse(json) is JsonObject startRoot && startRoot["history"] is JsonArray intro && intro.Count > 0)
            {
                startRoot.Remove("history");
                _pendingIntro = intro.ToJsonString(_serializeOptions);
                json = startRoot.ToJsonString(_serializeOptions);
            }
            _settings.History = [];   // the conversation in the hero editor is not part of the game
            _storage.ApplyUpdateWorldState(json);

            // World created successfully — commit it to the live save file
            _storage.Save();
            Storage.DeleteDraft();
        }
        catch (Exception ex)
        {
            RecordError(ex, response);
        }
    }

    // The location block the hero approached (LocationGrower.UnpopulatedNearHero) — the model names the rooms and places
    // creatures/objects. The geometry is already generated by the code and is in the map.
    public async Task PopulateChunk(
        MapChunk chunk,
        Action<string>? onChunk = null,
        Action<string>? onNewMessage = null,
        Action? onIdle = null,
        Action? onRedraw = null)
    {
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(PopulateChunk));
        var ctx = new AiContextBuilder(_settings, _storage.VisibleCells);
        string userMessage = ctx.MinimalState("(new location block — populate it)") + "\n\n" + ctx.LocationPlan(chunk) + "\n" + ctx.ChunkState(chunk);
        string response = "";
        try
        {
            _storage.Save(); // MCP get_world_state reads the save
            response = await CompleteWithSpinner([ReadPrompt(actionPath)], userMessage, actionPath, _appConfig.ClaudeModel, onIdle);
            await PlayResponse(ExtractJson(response), onChunk, onNewMessage, onRedraw);
            chunk.Populated = true;
            _storage.Save();
        }
        catch (Exception ex)
        {
            RecordError(ex, response);
        }
    }

    public async Task SendAction(
        string playerAction,
        Action<string>? onChunk = null,
        Action<string>? onNewMessage = null,
        Action? onIdle = null,
        Action? onRedraw = null)
    {
        var (actionPath, systemBlocks, ctx) = BuildSendActionContext();
        Helpers.Speech.Stop();
        string response = "";
        try
        {
            _storage.Save(); // MCP (plan_location, add_place, world_query) reads the save
            // The DM didn't describe the previous journey (server didn't respond) — that first, then the player's action.
            string pending = _settings.PendingTravel is { Length: > 0 } pt ? pt + "\n\n## Player action after the journey\n" : "";
            response = await CompleteWithSpinner(systemBlocks, ctx.MinimalState(pending + playerAction), actionPath, _appConfig.ClaudeModel, onIdle);
            ReloadPlannedLocation();
            _settings.PendingTravel = null;
            await PlayResponse(ExtractJson(response), onChunk, onNewMessage, onRedraw);
            _storage.Save();
        }
        catch (Exception ex)
        {
            RecordError(ex, response);
        }
    }

    // Travel on the world map (TravelService has already moved the hero and advanced time): one DM call —
    // describe the road, rests, supplies, the encounter (if rolled) and the arrival; if needed it builds a scene (plan_location).
    public async Task Travel(
        string travelReport,
        Action<string>? onChunk = null,
        Action<string>? onNewMessage = null,
        Action? onIdle = null,
        Action? onRedraw = null)
    {
        var (actionPath, systemBlocks, ctx) = BuildSendActionContext();
        string response = "";
        try
        {
            _storage.Save();
            // A previous journey the DM didn't describe — together with this one (its outcome is needed too).
            if (_settings.PendingTravel is { Length: > 0 } earlier) travelReport = earlier + "\n\n" + travelReport;
            _settings.PendingTravel = travelReport;   // until the DM responds, the journey is "undescribed"
            response = await CompleteWithSpinner(systemBlocks, ctx.MinimalState(travelReport), actionPath, _appConfig.ClaudeModel, onIdle);
            ReloadPlannedLocation();
            _settings.PendingTravel = null;
            await PlayResponse(ExtractJson(response), onChunk, onNewMessage, onRedraw);
            _storage.Save();
        }
        catch (Exception ex)
        {
            RecordError(ex, response, L.T("Travel error"));
            _settings.History.Add(new DialogMessage { Text = L.T("The DM didn't respond, but the journey is done: the road, food and encounters will be described with your next action.") });
            _storage.Save();
        }
    }

    // The DM built a location mid-game (MCP plan_location wrote it to the save and left a flag) —
    // reload the map and the hero's position, otherwise the next Save would overwrite it with the in-memory state.
    private void ReloadPlannedLocation()
    {
        string flag = Path.Combine(Path.GetDirectoryName(Storage.SavePath) ?? ".", Storage.LocationPlannedFlag);
        if (!File.Exists(flag)) return;
        try { File.Delete(flag); } catch { }
        try
        {
            _storage.ReloadMapFrom(Storage.SavePath);
            if (_settings.Map.Chunks?.FirstOrDefault(c => c.Exits.Any(e => e.External)) is { } start) start.Populated = true;
        }
        catch { /* map didn't reload — keep playing on the old one */ }
    }

    public async Task FireEnemyTurn(
        Action<string>? onChunk = null,
        Action<string>? onNewMessage = null,
        Action? onIdle = null,
        Action? onRedraw = null)
    {
        var (actionPath, systemBlocks, ctx) = BuildSendActionContext();
        string response = "";
        try
        {
            response = await CompleteWithSpinner(systemBlocks, ctx.CombatTurnState(), actionPath, _appConfig.ClaudeModel, onIdle);
            await PlayResponse(ExtractJson(response), onChunk, onNewMessage, onRedraw);
            _storage.Save();
        }
        catch (Exception ex)
        {
            RecordError(ex, response, L.T("Enemy turn error"));
        }
    }

    public async Task FireRoundTransition(
        RoundBundle bundle,
        Action<string>? onChunk = null,
        Action<string>? onNewMessage = null,
        Action? onIdle = null,
        Action? onRedraw = null)
    {
        var (actionPath, systemBlocks, ctx) = BuildSendActionContext();
        string response = "";
        try
        {
            response = await CompleteWithSpinner(systemBlocks, ctx.RoundTransitionState(bundle), actionPath, _appConfig.ClaudeModel, onIdle);
            await PlayResponse(ExtractJson(response), onChunk, onNewMessage, onRedraw);
            _storage.Save();
        }
        catch (Exception ex)
        {
            RecordError(ex, response, L.T("Round error"));
        }
    }

    public async Task FireTrigger(
        List<(CellEntity entity, string triggerType)> triggers,
        List<Area> areaTriggers,
        List<(Door door, string triggerType)> doorTriggers,
        Action<string>? onChunk = null,
        Action<string>? onNewMessage = null,
        Action? onIdle = null,
        Action? onRedraw = null)
    {
        var oneShotPatches = BuildOneShotPatches(triggers);
        oneShotPatches.AddRange(BuildAreaOneShotPatches(areaTriggers));
        oneShotPatches.AddRange(BuildDoorOneShotPatches(doorTriggers));

        var (actionPath, systemBlocks, ctx) = BuildSendActionContext();
        string response = "";
        try
        {
            bool wasInCombat = _settings.Combat?.Active == true;
            response = await CompleteWithSpinner(systemBlocks, ctx.TriggerState(triggers, areaTriggers, doorTriggers), actionPath, _appConfig.ClaudeModel, onIdle);
            await PlayResponse(ExtractJson(response), onChunk, onNewMessage, onRedraw);
            if (!wasInCombat && _settings.Combat?.Active == true && _settings.Hero != null)
            {
                _settings.Hero.SpeedLeft = _settings.Hero.SpeedMax ?? 30;
                _storage.ResetDiagonalCount();
            }

            foreach (var patch in oneShotPatches)
                _storage.ApplyUpdateWorldState(patch);

            _storage.Save();
        }
        catch (Exception ex)
        {
            RecordError(ex, response, L.T("Trigger error"));
        }
    }

    // Only the fired sub-key is removed (Storage.MergeTriggers) — the object's other triggers
    // are left alone. spotted/opportunity_attack_on_hero are not one-shot and never get here.
    private static TriggerEffect? OneShotEffect(CellTriggers? t, string triggerType) => triggerType switch
    {
        "onStep" => t?.OnStep,
        "onVisible" => t?.OnVisible,
        _ => null,
    };

    private static string RemoveTriggerPatch(string listKey, int id, string triggerKey) =>
        $"{{\"map\":{{\"{listKey}\":[{{\"id\":{id},\"triggers\":{{\"{triggerKey}\":null}}}}]}}}}";

    private List<string> BuildOneShotPatches(List<(CellEntity entity, string triggerType)> triggers)
    {
        var patches = new List<string>();
        foreach (var (entity, triggerType) in triggers)
        {
            if (OneShotEffect(entity.Triggers, triggerType)?.Once != true) continue;

            var inEntities = entity is LivingEntity le ? (_settings.Map.Entities?.IndexOf(le) ?? -1) : -1;
            var inObjects = inEntities < 0 ? (_settings.Map.Objects?.IndexOf(entity) ?? -1) : -1;
            int id = inEntities >= 0 ? inEntities : inObjects;
            if (id < 0) continue;

            patches.Add(RemoveTriggerPatch(inEntities >= 0 ? "entities" : "objects", id, triggerType));
        }
        return patches;
    }

    private List<string> BuildAreaOneShotPatches(List<Area> areas)
    {
        var patches = new List<string>();
        foreach (var area in areas)
        {
            bool isRoom = area is Room room && (_settings.Map.Rooms?.Contains(room) == true);
            int id = isRoom
                ? _settings.Map.Rooms!.IndexOf((Room)area)
                : (_settings.Map.Area?.IndexOf(area) ?? -1);
            if (id < 0) continue;

            patches.Add(RemoveTriggerPatch(isRoom ? "rooms" : "area", id, "onExplored"));
        }
        return patches;
    }

    private List<string> BuildDoorOneShotPatches(List<(Door door, string triggerType)> doorTriggers)
    {
        var patches = new List<string>();
        foreach (var (door, triggerType) in doorTriggers)
        {
            if (OneShotEffect(door.Triggers, triggerType)?.Once != true) continue;

            int id = _settings.Map.Doors?.IndexOf(door) ?? -1;
            if (id < 0) continue;

            patches.Add(RemoveTriggerPatch("doors", id, triggerType));
        }
        return patches;
    }

    private async Task CallAction(string userMessage, string actionName, string model, Action? onIdle = null)
    {
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, actionName);
        string systemPrompt = ReadPrompt(actionPath);

        string response = "";
        try
        {
            response = await CompleteWithSpinner([systemPrompt], userMessage, actionPath, model, onIdle);
            _storage.ApplyUpdateWorldState(await EnsureValidJson(ExtractJson(response)));
            AddEngineNotices();
            if (actionName is nameof(StartNewGame) or nameof(SendAction))
                _storage.Save();
        }
        catch (Exception ex)
        {
            RecordError(ex, response);
        }
    }

    private (string actionPath, IReadOnlyList<string> systemBlocks, AiContextBuilder ctx) BuildSendActionContext()
    {
        string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, nameof(SendAction));
        string instructions = ReadPrompt(actionPath);
        var ctx = new AiContextBuilder(_settings, _storage.VisibleCells);
        return (actionPath, [ctx.Instructions(instructions)], ctx);
    }

    private async Task<string> CompleteWithSpinner(
        IReadOnlyList<string> systemBlocks, string userMessage, string actionPath, string model, Action? onIdle)
    {
        if (onIdle == null)
            return await CompleteConfigured(systemBlocks, userMessage, actionPath, model);

        using var cts = new CancellationTokenSource();
        var spinTask = SpinAsync(onIdle, this, _rollRequestPath, _askPlayerRequestPath, cts.Token);
        try
        {
            return await CompleteConfigured(systemBlocks, userMessage, actionPath, model);
        }
        finally
        {
            cts.Cancel();
            await spinTask;
        }
    }

    private static async Task SpinAsync(
        Action onIdle,
        GameAiClient client, string rollRequestPath,
        string askPlayerRequestPath,
        CancellationToken ct)
    {
        const int IdleIntervalMs = 100;
        const int PollIntervalMs = 16;
        var nextIdleTick = DateTime.UtcNow;
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                if (now >= nextIdleTick)
                {
                    nextIdleTick = now.AddMilliseconds(IdleIntervalMs);
                    onIdle();

                    if (File.Exists(rollRequestPath))
                    {
                        try
                        {
                            string json = await File.ReadAllTextAsync(rollRequestPath, ct);
                            var req = JsonSerializer.Deserialize<RollRequest>(json, _rollJsonOptions);
                            if (req != null && !req.Answered)
                            {
                                int answer = client.ActiveDialog != null
                                    ? await client.ActiveDialog.PlayDiceRollRequest(req)
                                    : 1;
                                req.Answered = true;
                                req.Answer = answer;
                                await File.WriteAllTextAsync(rollRequestPath,
                                    JsonSerializer.Serialize(req, _rollJsonOptions), ct);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }

                    if (File.Exists(askPlayerRequestPath))
                    {
                        try
                        {
                            string json = await File.ReadAllTextAsync(askPlayerRequestPath, ct);
                            var req = JsonSerializer.Deserialize<AskPlayerRequest>(json, _rollJsonOptions);
                            if (req != null && !req.Answered)
                            {
                                string? answer = client.ActiveDialog != null
                                    ? await client.ActiveDialog.PlayAskPlayerRequest(req)
                                    : null;
                                req.Answered = true;
                                req.Answer = answer;
                                await File.WriteAllTextAsync(askPlayerRequestPath,
                                    JsonSerializer.Serialize(req, _rollJsonOptions), ct);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }

                    if (File.Exists(_selectTargetRequestPath))
                    {
                        try
                        {
                            string json = await File.ReadAllTextAsync(_selectTargetRequestPath, ct);
                            var req = JsonSerializer.Deserialize<TargetSelectionRequest>(json, _rollJsonOptions);
                            if (req != null && !req.Answered)
                            {
                                req.Result = client.ActiveDialog != null
                                    ? await client.ActiveDialog.PlayTargetSelectionRequest(req)
                                    : new TargetSelectionResult { Cancelled = true };
                                req.Answered = true;
                                await File.WriteAllTextAsync(_selectTargetRequestPath,
                                    JsonSerializer.Serialize(req, _rollJsonOptions), ct);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                }
                else
                {
                    // Between spinner ticks: fast mouse/keyboard poll without dialog rerender.
                    client.ActiveDialog?.PollOnly();
                }

                await Task.Delay(PollIntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // New-game intro — waits for the game screen (MapScreenLoop), where it is typed out character by character.
    private string? _pendingIntro;
    public bool HasPendingIntro => _pendingIntro != null;

    public async Task PlayIntro(Action<string>? onChunk, Action<string>? onNewMessage, Action? onRedraw)
    {
        if (_pendingIntro is not { } intro) return;
        _pendingIntro = null;
        try
        {
            await PlayResponse("{\"history\":" + intro + "}", onChunk, onNewMessage, onRedraw);
        }
        catch (Exception ex)
        {
            RecordError(ex, intro);
        }
        _storage.Save();
    }

    private async Task PlayResponse(
        string json,
        Action<string>? onChunk,
        Action<string>? onNewMessage,
        Action? onRedraw)
    {
        json = await EnsureValidJson(json);
        var player = new SequentialHistoryPlayer(
            _settings, _storage, _serializeOptions, onChunk, onNewMessage, onRedraw,
            onPoll: ActiveDialog != null ? ActiveDialog.PollOnly : null,
            animations: ActiveDialog?.Animations);
        var history = await player.PlayAsync(json);
        (_settings.History ??= []).AddRange(history);
        AddEngineNotices();
    }

    // Engine messages (XP awarded, level available) — after the DM's entries.
    private void AddEngineNotices()
    {
        foreach (string notice in _storage.TakeNotices())
            (_settings.History ??= []).Add(new DialogMessage { Text = notice });
    }

    // A DM response with broken JSON (extra/missing quote, comma, truncation) is parsed in full before
    // anything is applied to the game, so it can be repaired instead of losing the turn. Not a retry of the whole request: that would
    // ask the player for dice rolls and answers all over again. A short call without tools fixes only the syntax.
    // In UI tests with mocks — no repair (a broken response is tested there on purpose, and the real model must not be called).
    private async Task<string> EnsureValidJson(string json)
    {
        if (IsValidJson(json) || _appConfig.MockedActions.Length > 0) return json;
        try
        {
            string actionPath = Path.Combine(AppConfig.ProjectRoot, "Prompts", _ruleSet, "RepairJson");
            const string repairPrompt = "You fix JSON syntax. You are given broken JSON (extra or missing quotes, " +
                "commas, brackets, unescaped characters in strings, truncation at the end). Return the same JSON with corrected " +
                "syntax: the same keys, values and texts; add nothing and remove nothing in meaning. Truncated — close " +
                "the unfinished string/array/object. Response — JSON only, no explanations and no ```.";
            string fixedJson = ExtractJson(await _provider.Complete([repairPrompt], json, actionPath, _appConfig.RepairJsonModel));
            if (IsValidJson(fixedJson)) return fixedJson;
        }
        catch { /* repair failed — parse error below, as before */ }
        return json;
    }

    private static bool IsValidJson(string json)
    {
        try { using var _ = JsonDocument.Parse(json); return true; }
        catch (JsonException) { return false; }
    }

    // AI log (Program): AI errors are written there in full — the player gets a short version in the dialog.
    public AiLogger? Logger { get; set; }

    // Details only with the "AI ERRORS: DETAILED" setting; otherwise the player gets a short note on what to do. An AI setup
    // error (CLI not found) always shows its own text: a retry won't help. Full details are in the AI log.
    private void RecordError(Exception ex, string response, string? prefix = null)
    {
        prefix ??= L.T("Error");
        Logger?.LogNote($"{prefix}: {ex}" + (response.Length > 0 ? $"\nResponse: {response}" : ""));
        if ((ex as AiSetupException ?? ex.InnerException as AiSetupException) is { } setup)
        {
            _settings.History.Add(new DialogMessage { Text = $"{prefix}: {setup.Message}" });
            return;
        }
        string preview = response.Length > 200 ? response[..200] : response;
        preview = preview.Replace("\r", "").Replace("\n", " ");
        _settings.History.Add(new DialogMessage
        {
            Text = _appConfig.ShowAiErrors || _appConfig.MockedActions.Length > 0
                ? $"{prefix}: {ex.Message} " + L.T("Response: ") + preview
                : $"{prefix}: " + L.T("the DM stumbled and couldn't respond — repeat the action."),
        });
    }

    private string GetPassivePerception()
    {
        var skill = _settings.Hero?.Skills?.FirstOrDefault(s => SkillNames.IsPerception(s.Name));

        if (skill?.Value == null) return "10";
        return $"10 {skill.Value}";
    }

    private static string ReadPrompt(string actionPath) =>
        File.ReadAllText(Path.Combine(actionPath, "systemPrompt.md"), Encoding.UTF8);

    private async Task<string> CompleteConfigured(IReadOnlyList<string> systemBlocks, string userMessage, string actionPath, string model)
    {
        try
        {
            return await _provider.Complete(systemBlocks,
                $"lang:{L.World}\nspeechEnabled:{(_appConfig.SpeechEnabled ? "true" : "false")}\n{userMessage}", actionPath, model);
        }
        finally { WindowControls.NotifyIfMinimized(); }
    }

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

        // Truncated JSON — close remaining open braces
        return text[start..] + new string('}', depth);
    }
}

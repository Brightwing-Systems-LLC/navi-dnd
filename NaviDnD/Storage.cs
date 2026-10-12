using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using NaviDnD.Data.Models;

namespace NaviDnD;

public class Storage
{
    public HashSet<(int X, int Y)> ExploredCells = [];

    // Map items the hero has already seen ("name@col,row"): in the fog they are remembered as a dim symbol.
    // null — not yet seeded after loading (the first frame treats items on explored cells as seen).
    public HashSet<string>? SeenObjects;

    public static string ObjectKey(CellEntity o) => $"{o.Name}@{o.Position[0]},{o.Position[1]}";

    // An item (not a creature) is remembered by the hero: it lies on an explored cell and the hero has already seen it.
    public bool IsRemembered(CellEntity o) =>
        o is not LivingEntity && o.Position is { Count: >= 2 } p && ExploredCells.Contains((p[0], p[1]))
        && SeenObjects?.Contains(ObjectKey(o)) == true;
    public HashSet<(int, int)> VisibleCells = [];

    // Whether to keep ExploredCells between runs (see AppConfig.SaveExploredCells) — set by
    // the owner of Storage (Program.cs); true by default so that other places (MCP tools, tests)
    // that create Storage() without this step keep the default behaviour.
    public bool SaveExploredCells { get; set; } = true;

    public NewGameData NewGameData { get; set; }

    public WorldState WorldState { get; private set; } = new WorldState();
    public int DiagonalUsed { get; private set; } = 0;
    public void ResetDiagonalCount() => DiagonalUsed = 0;

    private Dictionary<string, List<int>> _entityColors = [];
    private bool _wasSpeedExhausted = false;
    private readonly Dictionary<int, int> _areaRoundFired = [];
    private readonly HashSet<string> _firedEffectIds = [];
    private readonly Dictionary<string, int> _firedEffectOnRoundIds = [];
    private readonly HashSet<int> _firedScheduledEventIds = [];
    private readonly Dictionary<string, int> _oaOnHeroLastRound = [];
    private readonly HashSet<string> _entitiesSeeingHero = [];
    private readonly Dictionary<string, int> _entityRoundFired = [];

    // One opportunity attack per creature per round (the creature's reaction). Returns true on the first trigger.
    public bool TryMarkOpportunityAttackOnHero(string symbol, int round)
    {
        if (_oaOnHeroLastRound.TryGetValue(symbol, out int last) && last == round) return false;
        _oaOnHeroLastRound[symbol] = round;
        return true;
    }

    // The state lives here rather than in MovementHandler: that one is recreated after every AI call,
    // so deduplication there effectively did not work (spotted fired on every step).
    // Returns true only on the "did not see → sees" transition.
    public bool UpdateEntitySeesHero(string symbol, bool sees)
    {
        if (!sees)
        {
            _entitiesSeeingHero.Remove(symbol);
            return false;
        }
        return _entitiesSeeingHero.Add(symbol);
    }

    private static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return "   ";

        if (symbol.Length > 3)
            return symbol[..3];

        return symbol.Length < 3 ? symbol.PadRight(3, ' ') : symbol;
    }

    public List<int>? GetColorByName(string name)
    {
        return _entityColors.GetValueOrDefault(name);
    }

    public void ResetWorldState()
    {
        // A new game uses the chosen world's language (the new game wizard sets L.World when the world is chosen).
        WorldState.Language = L.World;
        WorldState.FormatVersion = StorageFormat.Current;
        WorldState.Hero = null;
        WorldState.Map = new MapConfig();
        WorldState.Narrative = new Narrative();
        WorldState.History = [];
        WorldState.Time = new GameTime();
        WorldState.Combat = null;
        WorldState.Images = null;
        WorldState.ActiveImageKey = null;
        WorldState.ScheduledEvents = [];
        _entityColors.Clear();
        ExploredCells.Clear();
        SeenObjects = null;
        _wasSpeedExhausted = false;
        DiagonalUsed = 0;
        _areaRoundFired.Clear();
        _firedEffectIds.Clear();
        _firedEffectOnRoundIds.Clear();
        _firedScheduledEventIds.Clear();
        _oaOnHeroLastRound.Clear();
        _entitiesSeeingHero.Clear();
        _entityRoundFired.Clear();
    }

    public RoundBundle? TakeRoundBundle()
    {
        int currentRound = WorldState.Time.TotalRounds;

        var expired = new List<EffectRef>();
        var onRound = new List<EffectRef>();

        if (WorldState.Hero != null)
            CollectEntityEffects("hero", null, WorldState.Hero, currentRound, expired, onRound);

        var entities = WorldState.Map.Entities;
        if (entities != null)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity.Deleted == true) continue;
                CollectEntityEffects("map.entities", i, entity, currentRound, expired, onRound);
            }
        }

        var scheduled = new List<(int, ScheduledEvent)>();
        var events = WorldState.ScheduledEvents;
        if (events != null)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev.Deleted == true) continue;
                if (currentRound >= ev.FireAtRound && _firedScheduledEventIds.Add(i))
                    scheduled.Add((i, ev));
            }
        }

        var areas = CollectAreaRoundTriggers(currentRound);
        var entityTriggers = CollectEntityRoundTriggers(currentRound);

        if (expired.Count == 0 && onRound.Count == 0 && scheduled.Count == 0 && areas.Count == 0
            && entityTriggers.Count == 0)
            return null;

        return new RoundBundle
        {
            NewRound = currentRound,
            ExpiredEffects = expired,
            OnRoundEffects = onRound,
            FiredScheduledEvents = scheduled,
            AreaRoundTriggers = areas,
            EntityRoundTriggers = entityTriggers
        };
    }

    // Creatures' triggers.onRound — once per round, only if the creature sees the hero and is not in initiative
    // (in combat it acts on its own turn anyway).
    private List<(int, LivingEntity)> CollectEntityRoundTriggers(int currentRound)
    {
        var result = new List<(int, LivingEntity)>();
        var entities = WorldState.Map.Entities;
        if (entities == null) return result;

        var inInitiative = WorldState.Combat?.Active == true
            ? WorldState.Combat.Initiative?.Where(e => e.Deleted != true).Select(e => e.Symbol).ToHashSet()
            : null;

        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e.Deleted == true || e.Hidden == true || e.Triggers?.OnRound == null) continue;
            if (inInitiative?.Contains(e.Symbol) == true) continue;
            if (_entityRoundFired.TryGetValue(e.Symbol, out int last) && last >= currentRound) continue;
            if (!MovementCalculator.EntitySeesHero(WorldState, e)) continue;

            _entityRoundFired[e.Symbol] = currentRound;
            result.Add((i, e));
        }
        return result;
    }

    private void CollectEntityEffects(
        string ownerPath, int? entityId, CellEntity owner, int currentRound,
        List<EffectRef> expired, List<EffectRef> onRound)
    {
        var effects = owner.Effects;
        if (effects == null) return;

        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];
            if (e.Deleted == true) continue;
            string key = $"{ownerPath}:{entityId}:{i}";

            if (e.ExpiresAtRound.HasValue && currentRound > e.ExpiresAtRound.Value)
            {
                if (e.OnExpire != null)
                {
                    if (_firedEffectIds.Add(key))
                        expired.Add(new EffectRef(ownerPath, entityId, owner.Name, i, e));
                }
                else
                {
                    // Expired, but there is nothing to tell the AI (no onExpire) — do not leave a "ghost" in the effects
                    // list forever with a frozen, expired duration.
                    e.Deleted = true;
                }
            }
            else if (e.OnRound != null)
            {
                if (!_firedEffectOnRoundIds.TryGetValue(key, out int lastRound) || lastRound < currentRound)
                {
                    _firedEffectOnRoundIds[key] = currentRound;
                    onRound.Add(new EffectRef(ownerPath, entityId, owner.Name, i, e));
                }
            }
        }
    }

    private List<(int, Area)> CollectAreaRoundTriggers(int currentRound)
    {
        var result = new List<(int, Area)>();
        var heroPos = WorldState.Hero?.Position;
        if (heroPos == null || heroPos.Count < 2) return result;

        int heroCol = heroPos[0], heroRow = heroPos[1];
        var areas = WorldState.Map.Area ?? [];
        for (int i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            if (area.Deleted == true || area.Triggers?.OnRound == null) continue;
            if (area.Positions?.Any(p => p.Count >= 2 && p[0] == heroCol && p[1] == heroRow) != true) continue;
            if (!_areaRoundFired.TryGetValue(i, out int lastRound) || lastRound < currentRound)
            {
                _areaRoundFired[i] = currentRound;
                result.Add((i, area));
            }
        }
        return result;
    }

    // Applying a save (our own, not a DM response): fields maintained only by the engine (hero.level/xp) are taken
    // from the file. A DM response does not change them — the DM awards XP with an xpAward patch.
    private bool _fromSave;

    // A save or a library hero card — like ApplyUpdateWorldState, but including the engine's fields.
    public void ApplySavedState(string json)
    {
        _fromSave = true;
        try { ApplyUpdateWorldState(json); }
        finally { _fromSave = false; }
    }

    // Engine messages for the history (XP awarded, level up available) — after the DM response's entries:
    // GameAiClient collects them once the response is shown.
    private readonly List<string> _notices = [];

    public List<string> TakeNotices()
    {
        var list = _notices.ToList();
        _notices.Clear();
        return list;
    }

    public void ApplyUpdateWorldState(string jsonUpdate)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonUpdate);
        // Whether combat was on before the patch: the last kill and "combat over" arrive in one patch, and combat:null may
        // be applied before the enemy is removed from the map (LeaveBodies).
        _combatAtPatchStart = WorldState.Combat?.Active == true;
        // LenientStringListConverter: the AI sometimes sends a string list as a single string ("aiInfo": "text") —
        // accept it as a one-element list instead of failing the whole response.
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new LenientStringListConverter() } };
        var root = doc.RootElement;

        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name.ToLower())
            {
                case "map":
                    ApplyMapUpdate(prop.Value, options);
                    break;

                case "hero":
                    ApplyHero(prop.Value, options);
                    break;

                case "narrative":
                    ApplyNarrativeUpdate(prop.Value, options);
                    break;

                // A note from the DM for the player's notes: only appended to a separate file (PlayerNotes),
                // it never enters WorldState — the AI cannot read the notes.
                case "playernote":
                    foreach (var text in prop.Value.ValueKind == JsonValueKind.Array
                                 ? prop.Value.EnumerateArray().Select(e => e.GetString()) : [prop.Value.GetString()])
                        if (!string.IsNullOrWhiteSpace(text))
                            PlayerNotes.Add(text, WorldState.Time.Day, WorldState.Time.PartOfDay, byMaster: true);
                    break;

                case "time":
                    ApplyTimeUpdate(prop.Value);
                    break;

                // A journey the DM has not narrated (from the save) — goes out with the next action.
                case "pendingtravel":
                    WorldState.PendingTravel = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    break;

                // The game's world: from a save (has an id) — as is; from the DM — a patch (concept, kingdoms,
                // chronicle places, where the hero is now — GameWorld.Apply).
                case "world":
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.EnumerateObject().Any(p => p.Name.Equals("id", StringComparison.OrdinalIgnoreCase)))
                        WorldState.World = prop.Value.Deserialize<GameWorldLink>(options);
                    else
                        GameWorld.Apply(WorldState, prop.Value);
                    break;

                case "scheduledevents":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        WorldState.ScheduledEvents = PatchByIndex(WorldState.ScheduledEvents, prop.Value, options);
                    break;

                case "history":
                    var newHistory = JsonSerializer.Deserialize<List<DialogMessage>>(prop.Value.GetRawText(), options);
                    foreach (var h in newHistory)
                    {
                        h.SpeechText = Helpers.Speech.ValidateSsml(h.Text);
                        h.Text = Helpers.Speech.DisplayText(h.Text);
                        h.Text = h.Text.Replace("\n", "");
                    }

                    WorldState.History.AddRange(newHistory);
                    break;

                case "combat":
                    ApplyCombat(prop.Value, options);
                    break;

                case "images":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        ApplyImages(prop.Value, options);
                    break;

                case "activeimagkey":
                    WorldState.ActiveImageKey = prop.Value.ValueKind == JsonValueKind.Null
                        ? null
                        : prop.Value.GetString();
                    break;

                // From the save file (the DM does not send these): the format and the game's language.
                case "formatversion":
                    if (prop.Value.ValueKind == JsonValueKind.Number) WorldState.FormatVersion = prop.Value.GetInt32();
                    break;

                case "language":
                    if (prop.Value.ValueKind == JsonValueKind.String) WorldState.Language = L.Normalize(prop.Value.GetString());
                    break;
            }
        }
        NormalizeKeys();
        L.SetWorld(WorldState.Language);
        FillMissingStats();
    }

    // The DM's words in any language → stable keys (Data/Models/Keys.cs): part of day, special hero stats,
    // resource categories. The code compares keys only.
    private void NormalizeKeys()
    {
        WorldState.Time.PartOfDay = PartsOfDay.Normalize(WorldState.Time.PartOfDay);
        if (WorldState.Hero is not { } hero) return;
        foreach (var stat in hero.Stats ?? []) stat.Key ??= StatKeys.FromName(stat.Name);
        foreach (var resource in hero.Resources ?? []) resource.Category = ResourceCategories.Normalize(resource.Category);
        // "Fighter 3 lvl" → "Fighter": the level lives in hero.level. The level is taken from the stat only for a hero without a level (hero
        // creation); for a hero with a level the DM cannot raise it this way.
        if (hero.Stat(StatKeys.Class) is { } cls && ClassLevel.Split(cls.Value) is (var name, int level))
        {
            cls.Value = name;
            if (hero.Level == null)
            {
                hero.Level = level;
                hero.Xp ??= Helpers.LevelRules.Current.XpFor(level);
            }
        }
    }

    // XP from the DM: {"monsters":["Goblin",…], "xp":N, "reason":"…"} — reference monsters are counted by Challenge
    // Rating (LevelRules), xp is for everything else (a quest, a find). The total goes into the history after the DM's response.
    private void ApplyXpAward(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || WorldState.Hero is not { } hero) return;
        var rules = Helpers.LevelRules.Current;
        int total = 0;
        string? reason = null;
        foreach (var p in el.EnumerateObject())
            switch (p.Name.ToLowerInvariant())
            {
                case "monsters" when p.Value.ValueKind == JsonValueKind.Array:
                    foreach (var m in p.Value.EnumerateArray())
                        if (m.ValueKind == JsonValueKind.String) total += rules.MonsterXp(m.GetString());
                    break;
                case "xp" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int xp):
                    total += Math.Max(0, xp);
                    break;
                case "reason" when p.Value.ValueKind == JsonValueKind.String:
                    reason = p.Value.GetString();
                    break;
            }
        if (total <= 0) return;
        bool could = rules.CanLevelUp(hero);
        hero.Xp = (hero.Xp ?? 0) + total;
        _notices.Add(string.IsNullOrWhiteSpace(reason) ? L.WF("[+{0} XP]", total) : L.WF("[+{0} XP: {1}]", total, reason.Trim()));
        if (!could && rules.CanLevelUp(hero))
            _notices.Add(L.W("[Level up available: CHARACTER → “Level up”]"));
    }

    // A creature removed from the map mid-combat (killed) leaves a body on its cell: an object with everything the DM knew
    // about it (aiInfo — a charm, a letter, a key) — otherwise loot and plot hooks vanished along with the enemy.
    // The DM handles searching and loot (§ End of combat). A body the DM placed itself is not duplicated.
    private bool _combatAtPatchStart;

    // A safeguard against a forgotten patch: the DM wrote a hit point change in the entry's mechanics ("[… damage 4, WL2 11→7]",
    // "HP 12→7") but did not send `hp` in the patch — the creature stayed unharmed despite the text. Only the format
    // "SYMBOL (or HP/ХП for the hero) before→after" inside [...] and only if it currently has exactly "before".
    public bool ReconcileMechanics(string text)
    {
        bool changed = false;
        foreach (System.Text.RegularExpressions.Match br in System.Text.RegularExpressions.Regex.Matches(text, @"\[([^\]]*)\]"))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(br.Groups[1].Value,
                         @"(?<![\w])([\wЁё]{2,3})\s*:?\s*(\d+)\s*(?:→|->)\s*(\d+)"))
            {
                string who = m.Groups[1].Value;
                LivingEntity? target = who.ToUpperInvariant() is "HP" or "ХП"
                    ? WorldState.Hero
                    : string.Equals(who, WorldState.Hero?.Symbol, StringComparison.OrdinalIgnoreCase)
                        ? WorldState.Hero
                        : WorldState.Map.Entities?.FirstOrDefault(e => e.Deleted != true && string.Equals(e.Symbol, who, StringComparison.OrdinalIgnoreCase));
                if (target?.Hp is not { Length: > 0 } hp) continue;
                var parts = hp.Split('/');
                int from = int.Parse(m.Groups[2].Value), to = int.Parse(m.Groups[3].Value);
                if (!int.TryParse(parts[0].Trim(), out int cur) || cur != from || cur == to) continue;
                target.Hp = parts.Length > 1 ? $"{to}/{parts[1].Trim()}" : to.ToString();
                changed = true;
            }
        return changed;
    }

    private void LeaveBodies(List<LivingEntity> before)
    {
        if (WorldState.Combat?.Active != true && !_combatAtPatchStart) return;
        var alive = WorldState.Map.Entities?.Where(e => e.Deleted != true).ToHashSet() ?? [];
        foreach (var dead in before.Where(e => !alive.Contains(e) && e.Position is { Count: >= 2 }))
        {
            if (dead == WorldState.Hero) continue;
            // The slain species goes into the journal's bestiary (previously it was filled only by a knowledge check at the DM's request).
            if (dead.MonsterKey is { Length: > 0 } key)
            {
                var known = (WorldState.Narrative ??= new Narrative()).KnownMonsters ??= [];
                if (!known.Contains(key, StringComparer.OrdinalIgnoreCase)) known.Add(key);
            }
            string name = L.WF("Body: {0}", dead.Name);
            var objects = WorldState.Map.Objects ??= [];
            if (objects.Any(o => o.Deleted != true && o.Position is { Count: >= 2 } p && p[0] == dead.Position[0] && p[1] == dead.Position[1]
                                 && o.Name?.Contains(dead.Name ?? "\0", StringComparison.OrdinalIgnoreCase) == true)) continue;
            objects.Add(new CellEntity
            {
                Symbol = "(†)",
                Name = name,
                Position = [dead.Position[0], dead.Position[1]],
                Color = [150, 150, 150],
                Image = dead.Image,
                AiInfo = [.. dead.AiInfo ?? [], L.WF("Killed in battle ({0})", dead.MonsterKey ?? dead.Name)],
            });
        }
    }

    // What the DM did not set, but without which combat and sight are wrong:
    //  - a bestiary creature's Hit Points (monsterKey): the reference average ("22 (3d8+9)" → "22/22") — otherwise damage to the enemy
    //    cannot be tracked between responses, and a wounded enemy is "fresh" again on the next call;
    //  - the hero's darkvision from their traits ("Darkvision 60 ft") — old heroes were created without darkvisionFt,
    //    and an elf saw 10 feet at night.
    // The DM placed the hero beyond the location edge, inside a rock/tree/furniture or in the void between rooms — the hero is "off the map"
    // or stuck. The nearest suitable cell (up to 6 cells around). Creatures — only out of the void/beyond the edge: in water or
    // a thicket they may be there by design (a drowned man in the surf).
    private void KeepOnWalkableCells()
    {
        var map = WorldState.Map;
        if (map.Chunks is not { Count: > 0 }) return;   // old-format maps — as is
        bool OnField(int c, int r) => c >= 1 && r >= 1 && c <= map.Cols && r <= map.Rows && map.ChunkAt(c, r) != null
                                      && (map.TerrainAt(c, r) != null || map.RoomAt(c, r) != null || map.AreasAt(c, r).Count > 0);
        bool Walkable(int c, int r) => OnField(c, r) && map.TerrainAt(c, r) is not { BlocksMove: true };
        bool Occupied(int c, int r, CellEntity self) =>
            (map.Entities ?? []).Any(e => e != self && e.Deleted != true && e.Position is { Count: >= 2 } p && p[0] == c && p[1] == r)
            || (WorldState.Hero != self && WorldState.Hero?.Position is { Count: >= 2 } h && h[0] == c && h[1] == r);
        void Fix(CellEntity who, Func<int, int, bool> ok)
        {
            if (who.Position is not { Count: >= 2 } p || ok(p[0], p[1])) return;
            for (int rad = 1; rad <= 6; rad++)
                for (int dr = -rad; dr <= rad; dr++)
                    for (int dc = -rad; dc <= rad; dc++)
                    {
                        if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != rad) continue;
                        int c = p[0] + dc, r = p[1] + dr;
                        if (!Walkable(c, r) || Occupied(c, r, who)) continue;
                        who.Position = [c, r];
                        return;
                    }
        }
        if (WorldState.Hero != null) Fix(WorldState.Hero, Walkable);
        foreach (var e in map.Entities ?? [])
            if (e.Deleted != true) Fix(e, OnField);
    }

    private void FillMissingStats()
    {
        KeepOnWalkableCells();
        // Those killed/gone from the map do not stay in the combat order (except the hero).
        if (WorldState.Combat?.Initiative is { Count: > 0 } init)
        {
            var onMap = WorldState.Map.Entities?.Where(e => e.Deleted != true).Select(e => e.Symbol).ToHashSet() ?? [];
            init.RemoveAll(i => i.Symbol != WorldState.Hero?.Symbol && !onMap.Contains(i.Symbol));
        }

        foreach (var e in WorldState.Map.Entities ?? [])
        {
            if (e.Deleted == true || Helpers.MonsterDatabase.Find(e.MonsterKey) is not { } m) continue;
            if (string.IsNullOrWhiteSpace(e.Hp) && System.Text.RegularExpressions.Regex.Match(m.Hp, @"^\s*(\d+)") is { Success: true } hp)
                e.Hp = $"{hp.Groups[1].Value}/{hp.Groups[1].Value}";
            // Walking speed is the first number in the reference ("4 клетки, летая 10 клеток" — a square is 5 ft; or "20 фт."):
            // otherwise calculate_movement moved everyone at 30 ft, and zombies ran as fast as the hero.
            // "0 клеток, полет 20 клеток" (flying only) — the first non-zero one.
            if (e.SpeedMax == null && System.Text.RegularExpressions.Regex.Matches(m.Speed, @"(\d+)\s*(\S*)")
                    .FirstOrDefault(x => x.Groups[1].Value != "0") is { } sp)
                e.SpeedMax = int.Parse(sp.Groups[1].Value) * ((sp.Groups[2].Value.StartsWith("клет", StringComparison.OrdinalIgnoreCase) || sp.Groups[2].Value.StartsWith("square", StringComparison.OrdinalIgnoreCase)) ? 5 : 1);
        }

        if (WorldState.Hero is { DarkvisionFt: null } hero && hero.Stat(StatKeys.Darkvision) is { Value: { } dv }
            && System.Text.RegularExpressions.Regex.Match(dv, @"(\d+)") is { Success: true } ft && int.Parse(ft.Groups[1].Value) > 0)
            hero.DarkvisionFt = int.Parse(ft.Groups[1].Value);
    }

    private void ApplyCombat(JsonElement el, JsonSerializerOptions options)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            WorldState.Combat = null;
            return;
        }
        if (el.ValueKind != JsonValueKind.Object) return;

        WorldState.Combat ??= new CombatState();

        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name.ToLower())
            {
                case "active":
                    WorldState.Combat.Active = prop.Value.GetBoolean();
                    break;
                case "currentturn":
                    string prevTurn = WorldState.Combat.CurrentTurn;
                    WorldState.Combat.CurrentTurn = prop.Value.GetString() ?? "";
                    // The turn passed to the hero from another participant — their actions, reaction and speed are restored
                    // at the start of their own turn (not at the round boundary: a reaction spent after the round began on
                    // someone else's turn was otherwise lost for the hero's whole turn). Loading a save (prevTurn empty) is not a turn.
                    if (prevTurn.Length > 0 && prevTurn != WorldState.Combat.CurrentTurn && WorldState.Combat.CurrentTurn == WorldState.Hero?.Symbol)
                    {
                        ResetActionsForNewRound();
                        if (WorldState.Hero.SpeedMax is int max) WorldState.Hero.SpeedLeft = max;
                    }
                    break;
                case "initiative":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        WorldState.Combat.Initiative = PatchByIndex(WorldState.Combat.Initiative, prop.Value, options);
                        // Deduplicate by symbol (no-id entries are appended by PatchByIndex, keep latest)
                        if (WorldState.Combat.Initiative?.Count > 1)
                            WorldState.Combat.Initiative = WorldState.Combat.Initiative
                                .GroupBy(e => e.Symbol)
                                .Select(g => g.Last())
                                .OrderByDescending(e => e.Score)
                                .ToList();
                    }
                    break;
            }
        }
    }

    private void ApplyMapUpdate(JsonElement el, JsonSerializerOptions options)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name.ToLower())
            {
                case "cols":
                    WorldState.Map.Cols = prop.Value.GetInt32();
                    break;

                case "rows":
                    WorldState.Map.Rows = prop.Value.GetInt32();
                    break;

                case "colors":
                    ApplyColors(prop.Value);
                    break;

                case "rooms":
                    WorldState.Map.Rooms = PatchByIndex(WorldState.Map.Rooms, prop.Value, options);
                    break;

                case "doors":
                    WorldState.Map.Doors = PatchByIndex(WorldState.Map.Doors, prop.Value, options, allowDelete: false);
                    break;

                case "area":
                    WorldState.Map.Area = PatchByIndex(WorldState.Map.Area, prop.Value, options);
                    break;

                case "entities":
                    var entitiesBefore = WorldState.Map.Entities?.Where(e => e.Deleted != true).ToList() ?? [];
                    WorldState.Map.Entities = PatchByIndex(WorldState.Map.Entities, prop.Value, options);
                    LeaveBodies(entitiesBefore);
                    foreach (var entity in WorldState.Map.Entities ?? [])
                    {
                        entity.Symbol = NormalizeSymbol(entity.Symbol ?? "");
                        if (entity.Color != null) _entityColors[entity.Name] = entity.Color;
                    }
                    break;

                case "objects":
                    WorldState.Map.Objects = PatchByIndex(WorldState.Map.Objects, prop.Value, options);
                    foreach (var obj in WorldState.Map.Objects ?? [])
                        obj.Symbol = NormalizeSymbol(obj.Symbol ?? "");
                    break;

                case "triggercells":
                    WorldState.Map.TriggerCells = JsonSerializer.Deserialize<List<TriggerCell>>(prop.Value.GetRawText(), options);
                    break;

                // Location blocks are engine service data (LocationGrower); the AI does not patch them; replaced whole.
                case "chunks":
                    WorldState.Map.Chunks = prop.Value.ValueKind == JsonValueKind.Null
                        ? null : JsonSerializer.Deserialize<List<MapChunk>>(prop.Value.GetRawText(), options);
                    break;

                // Furniture: like objects — by id (break it with deleted, overturn it with state, move it with positions, new — without id).
                case "furniture":
                    WorldState.Map.Furniture = PatchByIndex(WorldState.Map.Furniture, prop.Value, options);
                    break;

                // Stairs between floors are engine service data too; replaced whole.
                case "stairs":
                    WorldState.Map.Stairs = prop.Value.ValueKind == JsonValueKind.Null
                        ? null : JsonSerializer.Deserialize<List<StairLink>>(prop.Value.GetRawText(), options);
                    break;

                case "defaultimage":
                    WorldState.Map.DefaultImage = prop.Value.ValueKind == JsonValueKind.Null
                        ? null : prop.Value.GetString();
                    break;
            }
        }
    }

    private void ApplyColors(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        WorldState.Map.Colors ??= new ColorSetting();

        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name.ToLower())
            {
                case "mapbackground":
                    WorldState.Map.Colors.MapBackground = DeserializeIntList(prop.Value);
                    break;
                case "mapforeground":
                    WorldState.Map.Colors.MapForeground = DeserializeIntList(prop.Value);
                    break;
            }
        }
    }

    private void ApplyHero(JsonElement el, JsonSerializerOptions options)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        WorldState.Hero ??= new Hero();

        ValidateKeys<Hero>(el, "hero", "xpaward");

        var oldPosition = WorldState.Hero.Position?.ToList();
        bool speedLeftUpdated = false;

        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name.ToLower())
            {
                case "symbol":
                    WorldState.Hero.Symbol = NormalizeSymbol(prop.Value.GetString() ?? "");
                    break;
                case "name":
                    WorldState.Hero.Name = prop.Value.GetString()!;
                    break;
                case "hp":
                    WorldState.Hero.Hp = prop.Value.GetString()!;
                    break;
                case "vision":
                case "visionft":
                    if (prop.Value.ValueKind != JsonValueKind.Null)
                        WorldState.Hero.VisionFt = prop.Value.GetInt32();
                    break;
                case "inspiration":
                    WorldState.Hero.Inspiration = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetBoolean();
                    break;
                case "stealth":
                    WorldState.Hero.Stealth = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt32();
                    break;
                case "dead":
                    WorldState.Hero.Dead = prop.Value.ValueKind == JsonValueKind.True ? true : null;
                    break;
                // Maintained by the engine: only from the save; the DM goes through xpAward.
                case "level" when _fromSave:
                    WorldState.Hero.Level = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt32() : null;
                    break;
                case "xp" when _fromSave:
                    WorldState.Hero.Xp = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt32() : null;
                    break;
                case "level":
                case "xp":
                    Console.Error.WriteLine($"[AI ERROR] hero.{prop.Name} is engine-owned, ignoring (use xpAward).");
                    break;
                case "xpaward":
                    ApplyXpAward(prop.Value);
                    break;
                case "levelupdraft" when _fromSave:
                    WorldState.Hero.LevelUpDraft = prop.Value.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<LevelUpDraft>(prop.Value.GetRawText(), options) : null;
                    break;
                case "noopportunityattacksround":
                    WorldState.Hero.NoOpportunityAttacksRound = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt32();
                    break;
                case "light":
                    WorldState.Hero.Light = prop.Value.ValueKind == JsonValueKind.Null
                        ? null
                        : JsonSerializer.Deserialize<LightSource>(prop.Value.GetRawText(), options);
                    break;
                case "position":
                    WorldState.Hero.Position = DeserializeIntList(prop.Value);
                    break;
                case "color":
                    WorldState.Hero.Color = DeserializeIntList(prop.Value);
                    break;
                case "stats":
                    WorldState.Hero.Stats = PatchByIndex(WorldState.Hero.Stats, prop.Value, options);
                    break;
                case "skills":
                    WorldState.Hero.Skills = PatchByIndex(WorldState.Hero.Skills, prop.Value, options);
                    break;
                case "inventory":
                    WorldState.Hero.Inventory = PatchByIndex(WorldState.Hero.Inventory, prop.Value, options);
                    break;
                case "equipmentslots":
                    WorldState.Hero.EquipmentSlots = JsonSerializer.Deserialize<List<string>>(prop.Value.GetRawText(), options)!;
                    break;
                case "abilities":
                    WorldState.Hero.Abilities = PatchByIndex(WorldState.Hero.Abilities, prop.Value, options);
                    break;
                case "spells":
                    WorldState.Hero.Spells = PatchByIndex(WorldState.Hero.Spells, prop.Value, options);
                    break;
                case "resources":
                    WorldState.Hero.Resources = PatchByIndex(WorldState.Hero.Resources, prop.Value, options);
                    break;
                case "speedmax":
                    WorldState.Hero.SpeedMax = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt32();
                    break;
                case "speedleft":
                    WorldState.Hero.SpeedLeft = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt32();
                    speedLeftUpdated = true;
                    break;
                case "effects":
                    WorldState.Hero.Effects = PatchByIndex(WorldState.Hero.Effects, prop.Value, options);
                    break;
                case "actions":
                    WorldState.Hero.Actions = PatchByIndex(WorldState.Hero.Actions, prop.Value, options);
                    break;
                case "image":
                    WorldState.Hero.Image = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
                    break;
            }
        }

        bool positionChanged = oldPosition != null
            && WorldState.Hero.Position != null
            && !oldPosition.SequenceEqual(WorldState.Hero.Position);

        bool inCombat = WorldState.Combat?.Active == true;
        if (positionChanged && _wasSpeedExhausted && !inCombat)
        {
            WorldState.Time.TotalRounds++;
            _wasSpeedExhausted = false;
            ResetActionsForNewRound();
        }

        if (speedLeftUpdated && WorldState.Hero.SpeedLeft.HasValue)
        {
            _wasSpeedExhausted = !inCombat && WorldState.Hero.SpeedLeft.Value == 0;
            if (inCombat && WorldState.Hero.SpeedLeft.Value >= (WorldState.Hero.SpeedMax ?? 0) && WorldState.Hero.SpeedMax > 0)
                DiagonalUsed = 0;
        }
    }

    public void ApplyClientMovement(int distanceFeet, bool isDiagonal = false)
    {
        var hero = WorldState.Hero;
        if (hero == null || !hero.SpeedMax.HasValue) return;

        int speedMax  = hero.SpeedMax.Value;
        int speedLeft = hero.SpeedLeft ?? speedMax;
        bool inCombat = WorldState.Combat?.Active == true;

        if (!inCombat && speedLeft < distanceFeet)
        {
            WorldState.Time.TotalRounds++;
            speedLeft = speedMax;
            _wasSpeedExhausted = false;
            ResetActionsForNewRound();
        }

        if (isDiagonal) DiagonalUsed++;
        speedLeft = Math.Max(0, speedLeft - distanceFeet);
        hero.SpeedLeft = speedLeft;
        _wasSpeedExhausted = !inCombat && speedLeft == 0;
    }

    private void ResetActionsForNewRound()
    {
        DiagonalUsed = 0;
        var actions = WorldState.Hero?.Actions;
        if (actions == null) return;
        foreach (var a in actions)
            if (a.Deleted != true)
                a.Value = a.MaxValue;
    }

    private void ApplyTimeUpdate(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name.ToLower())
            {
                case "totalrounds":
                    WorldState.Time.TotalRounds = prop.Value.GetInt32();
                    break;
                case "partofday":
                    WorldState.Time.PartOfDay = prop.Value.GetString()!;
                    break;
                case "day":
                    WorldState.Time.Day = prop.Value.GetInt32();
                    break;
            }
        }
    }


    private static List<T> PatchByIndex<T>(List<T>? existing, JsonElement patches, JsonSerializerOptions options, bool allowDelete = true)
    {
        var result = (existing ?? []).ToList();
        if (patches.ValueKind != JsonValueKind.Array) return result;
        var toDelete = new List<int>();

        foreach (var patchEl in patches.EnumerateArray() )
        {
            ValidateKeys<T>(patchEl, "patch");

            if (!patchEl.TryGetProperty("id", out var idEl))
            {
                // no id → a new element, append it
                result.Add(JsonSerializer.Deserialize<T>(patchEl.GetRawText(), options)!);
                continue;
            }

            int id = idEl.GetInt32();
            bool deleted = patchEl.TryGetProperty("deleted", out var d) && d.GetBoolean();

            if (deleted && allowDelete)
                toDelete.Add(id);
            else if (!deleted && id < result.Count)
                result[id] = MergeInto(result[id], patchEl, options);
            else if (!deleted)
                result.Add(JsonSerializer.Deserialize<T>(patchEl.GetRawText(), options)!);
        }

        foreach (int id in toDelete.OrderByDescending(x => x))
            if (id < result.Count) result.RemoveAt(id);

        return result;
    }

    private void ApplyImages(JsonElement el, JsonSerializerOptions options)
    {
        WorldState.Images ??= [];
        foreach (var itemEl in el.EnumerateArray())
        {
            var patch = JsonSerializer.Deserialize<WorldImage>(itemEl.GetRawText(), options);
            if (patch == null || string.IsNullOrEmpty(patch.Key)) continue;

            int idx = WorldState.Images.FindIndex(img => img.Key == patch.Key);
            if (idx < 0)
            {
                if (patch.Deleted != true)
                    WorldState.Images.Add(patch);
            }
            else if (patch.Deleted == true)
            {
                WorldState.Images.RemoveAt(idx);
            }
            else
            {
                WorldState.Images[idx] = MergeInto(WorldState.Images[idx], itemEl, options);
            }
        }
    }

    private void ApplyNarrativeUpdate(JsonElement el, JsonSerializerOptions options)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        WorldState.Narrative ??= new Narrative();
        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name.ToLower())
            {
                case "world":
                    WorldState.Narrative.World = prop.Value.GetString()!;
                    break;
                case "currentarc":
                    WorldState.Narrative.CurrentArc = prop.Value.GetString()!;
                    break;
                case "style":
                    WorldState.Narrative.Style = prop.Value.GetString() ?? "";
                    break;
                case "plotthreads":
                    WorldState.Narrative.PlotThreads = PatchByIndex(WorldState.Narrative.PlotThreads, prop.Value, options);
                    break;
                case "npcs":
                    WorldState.Narrative.Npcs = PatchByIndex(WorldState.Narrative.Npcs, prop.Value, options);
                    break;
                // The bestiary only grows: the DM's list is added to what is known (species the engine added after
                // a kill would otherwise be wiped by the DM's next "whole list"). Knowledge is never forgotten — null does not clear it.
                case "knownmonsters":
                    if (prop.Value.ValueKind == JsonValueKind.Array
                        && JsonSerializer.Deserialize<List<string>>(prop.Value.GetRawText(), options) is { } incoming)
                    {
                        var known = WorldState.Narrative.KnownMonsters ??= [];
                        foreach (var m in incoming)
                            if (!string.IsNullOrWhiteSpace(m) && !known.Contains(m, StringComparer.OrdinalIgnoreCase)) known.Add(m);
                    }
                    break;
            }
        }
    }

    private static T MergeInto<T>(T existing, JsonElement patch, JsonSerializerOptions options)
    {
        var baseNode = JsonSerializer.SerializeToNode(existing, options)!.AsObject();
        foreach (var prop in patch.EnumerateObject())
        {
            if ((prop.Name.Equals("aiInfo", StringComparison.OrdinalIgnoreCase)
                 || prop.Name.Equals("memory", StringComparison.OrdinalIgnoreCase)
                 || prop.Name.Equals("steps", StringComparison.OrdinalIgnoreCase))
                && prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.String)
            {
                // A single string instead of an array (a common AI mistake) is appended just like an array containing it.
                var items = prop.Value.ValueKind == JsonValueKind.Array
                    ? prop.Value
                    : JsonDocument.Parse($"[{prop.Value.GetRawText()}]").RootElement;
                AppendList(baseNode, prop.Name, items);
            }
            else if (prop.Name.Equals("effects", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Array)
                MergeEffects(baseNode, prop.Value, options);
            else if (prop.Name.Equals("triggers", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object)
                MergeTriggers(baseNode, prop.Value);
            else
                baseNode[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        return baseNode.Deserialize<T>(options)!;
    }

    // "effects" inside a nested entity (map.entities[i].effects, map.objects[i].effects,
    // hero.inventory[i].effects) is patched by id just like the top-level hero.effects —
    // rather than replaced whole like other properties in MergeInto.
    private static void MergeEffects(JsonObject baseNode, JsonElement patch, JsonSerializerOptions options)
    {
        var existingNode = baseNode["effects"];
        var existing = existingNode != null
            ? existingNode.Deserialize<List<StatusEffect>>(options) ?? []
            : [];
        var merged = PatchByIndex(existing, patch, options);
        baseNode["effects"] = JsonSerializer.SerializeToNode(merged, options);
    }

    // triggers is patched by sub-key: {"triggers":{"spotted":null}} removes only spotted without
    // wiping onStep/onVisible of the same entity. {"triggers":null} (not an object) removes them all, as before.
    private static void MergeTriggers(JsonObject baseNode, JsonElement patch)
    {
        var existing = baseNode["triggers"] as JsonObject;
        bool isNew = existing == null;
        existing ??= new JsonObject(baseNode.Options);

        foreach (var t in patch.EnumerateObject())
            existing[t.Name] = t.Value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(t.Value.GetRawText());

        if (!existing.Any(kv => kv.Value != null))
            baseNode["triggers"] = null;
        else if (isNew)
            baseNode["triggers"] = existing;
    }

    // "Log" lists (aiInfo, npcs[i].memory, plotThreads[i].steps): a patch only appends lines rather than replacing the list.
    // Duplicates (the AI repeating what is already recorded) are not added.
    private static void AppendList(JsonObject baseNode, string patchKey, JsonElement newItems)
    {
        string? existingKey = null;
        JsonArray existingArr = [];
        foreach (var kv in baseNode)
        {
            if (kv.Key.Equals(patchKey, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = kv.Key;
                existingArr = kv.Value?.AsArray() ?? [];
                break;
            }
        }

        var merged = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var item in existingArr)
        {
            merged.Add(item?.DeepClone());
            seen.Add(item?.ToJsonString() ?? "");
        }
        foreach (var item in newItems.EnumerateArray())
            if (seen.Add(item.GetRawText()))
                merged.Add(JsonNode.Parse(item.GetRawText()));

        if (existingKey != null)
            baseNode.Remove(existingKey);
        baseNode[existingKey ?? char.ToLowerInvariant(patchKey[0]) + patchKey[1..]] = merged;
    }

    private static List<int> DeserializeIntList(JsonElement el) =>
        JsonSerializer.Deserialize<List<int>>(el.GetRawText())!;

    private static readonly Dictionary<Type, HashSet<string>> _knownKeys = new();

    private static void ValidateKeys<T>(JsonElement el, string context, params string[] extraKeys)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        if (!_knownKeys.TryGetValue(typeof(T), out var keys))
        {
            keys = typeof(T).GetProperties()
                .Select(p => p.Name.ToLower())
                .ToHashSet();
            _knownKeys[typeof(T)] = keys;
        }

        foreach (var prop in el.EnumerateObject())
        {
            string name = prop.Name.ToLower();
            if (name is "id" or "deleted" || extraKeys.Contains(name)) continue; // patch-protocol fields, not model properties
            if (!keys.Contains(name))
                Console.Error.WriteLine($"[AI ERROR] Unknown field '{prop.Name}' in {context} ({typeof(T).Name}), ignoring.");
        }
    }

    // ── Multiple games (the "MY GAMES" screen) ──────────────────────────────
    // Each game is Storage/game{N}.json (+ game{N}.explored.json), N = 1..MaxGames; in the same folder as
    // before — the MCP request files (roll_request.json etc.) sit next to the save, as they always did.
    // The active game is Storage/activeGame.txt. UI tests (NAVIDND_TEST_WORLDSTATE) still write to
    // worldState.json — GameSession backs it up and restores it.
    public const int MaxGames = 6;
    private static readonly string StorageDir = Path.Combine(AppConfig.ProjectRoot, "Storage");
    private static readonly string LegacySavePath = Path.Combine(StorageDir, "worldState.json");
    private static readonly string ActiveGamePath = Path.Combine(StorageDir, "activeGame.txt");
    private static bool TestMode => Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE") != null;

    public static int ActiveSlot { get; private set; } = 1;

    public static string SavePath => TestMode ? LegacySavePath : SlotPath(ActiveSlot);
    public static string SlotPath(int slot) => Path.Combine(StorageDir, $"game{slot}.json");
    public static bool SlotExists(int slot) => File.Exists(SlotPath(slot));
    // In UI tests the game writes to its own file (SavePath) and takes no game slot — a new game is always possible.
    public static int? FreeSlot() => TestMode ? 1 : Enumerable.Range(1, MaxGames).Cast<int?>().FirstOrDefault(s => !SlotExists(s!.Value));

    // At game start: the old single save (worldState.json) → game 1 (a copy, the original
    // stays), the active game comes from activeGame.txt; if there is none — the most recent one.
    public static void InitGames()
    {
        Directory.CreateDirectory(StorageDir);
        if (TestMode) return;
        try
        {
            if (!Enumerable.Range(1, MaxGames).Any(SlotExists) && File.Exists(LegacySavePath))
            {
                File.Copy(LegacySavePath, SlotPath(1));
                string legacyExplored = ExploredCellsPath(LegacySavePath);
                if (File.Exists(legacyExplored)) File.Copy(legacyExplored, ExploredCellsPath(SlotPath(1)), true);
            }
            if (File.Exists(ActiveGamePath) && int.TryParse(File.ReadAllText(ActiveGamePath).Trim(), out int saved)
                && saved is >= 1 and <= MaxGames)
                ActiveSlot = saved;
            if (!SlotExists(ActiveSlot))
            {
                var latest = Enumerable.Range(1, MaxGames).Where(SlotExists)
                    .OrderByDescending(s => File.GetLastWriteTime(SlotPath(s))).Cast<int?>().FirstOrDefault();
                if (latest is int l) ActiveSlot = l;
            }
            if (!File.Exists(ActiveGamePath)) File.WriteAllText(ActiveGamePath, ActiveSlot.ToString());
        }
        catch { /* not critical — play game 1 */ }
    }

    public static void SetActiveSlot(int slot)
    {
        ActiveSlot = Math.Clamp(slot, 1, MaxGames);
        if (TestMode) return;
        try { File.WriteAllText(ActiveGamePath, ActiveSlot.ToString()); } catch { }
    }

    public static void DeleteSlot(int slot)
    {
        try
        {
            if (File.Exists(SlotPath(slot))) File.Delete(SlotPath(slot));
            string explored = ExploredCellsPath(SlotPath(slot));
            if (File.Exists(explored)) File.Delete(explored);
            string notes = PlayerNotes.PathFor(SlotPath(slot));
            if (File.Exists(notes)) File.Delete(notes);
        }
        catch { }
    }

    // The draft for CreateNewGame/FixHeroForNewGame/StartNewGame — the MCP tools read/write here
    // until the new game is created successfully. The real SavePath is untouched until the final Save() in StartNewGame.
    public static readonly string DraftSavePath = Path.Combine(AppConfig.ProjectRoot, "Storage", "newGameState.json");

    private static readonly JsonSerializerOptions _saveOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static bool HasSave() => File.Exists(SavePath);

    public void Save() => SaveTo(SavePath);

    public void SaveDraft() => SaveTo(DraftSavePath);

    public static void DeleteDraft()
    {
        if (File.Exists(DraftSavePath)) File.Delete(DraftSavePath);
    }

    private void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(WorldState, _saveOptions);
        RetryOnIo(() =>
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(json);
        });

        SaveExploredCellsTo(path);
    }

    // A separate file next to worldState.json/newGameState.json — ExploredCells is not part of WorldState
    // (the AI never sees or patches it), so we do not mix it into the JSON-diff protocol.
    private static string ExploredCellsPath(string worldStatePath) =>
        Path.Combine(Path.GetDirectoryName(worldStatePath)!, Path.GetFileNameWithoutExtension(worldStatePath) + ".explored.json");

    private void SaveExploredCellsTo(string path)
    {
        string exploredPath = ExploredCellsPath(path);
        if (!SaveExploredCells)
        {
            try { if (File.Exists(exploredPath)) File.Delete(exploredPath); } catch { }
            return;
        }

        var cells = ExploredCells.Select(c => new[] { c.X, c.Y }).ToList();
        string exploredJson = JsonSerializer.Serialize(cells);
        RetryOnIo(() => File.WriteAllText(exploredPath, exploredJson, Encoding.UTF8));
    }

    private void LoadExploredCellsFrom(string path)
    {
        ExploredCells.Clear();
        SeenObjects = null;
        if (!SaveExploredCells) return;

        string exploredPath = ExploredCellsPath(path);
        if (!File.Exists(exploredPath)) return;
        try
        {
            var cells = JsonSerializer.Deserialize<List<int[]>>(File.ReadAllText(exploredPath, Encoding.UTF8));
            if (cells == null) return;
            foreach (var cell in cells)
                if (cell.Length == 2) ExploredCells.Add((cell[0], cell[1]));
        }
        catch { /* corrupt/incomplete — start with an empty fog of war, do not fail the load */ }
    }

    private static void RetryOnIo(Action action, int attempts = 5, int delayMs = 30)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(delayMs); }
        }
    }

    public void LoadSave() => LoadFrom(SavePath);

    public void LoadDraft() => LoadFrom(DraftSavePath);

    // Public — used by tests (NAVIDND_TEST_WORLDSTATE) to substitute
    // a test state for the real worldState.json. The general load path does not change.
    // The MCP plan_location marker (next to the save): the DM built a location mid-game — the map and the hero's position
    // must be re-read from the save before applying the DM's response (GameAiClient.ReloadPlannedLocation).
    public const string LocationPlannedFlag = "location_planned.flag";

    // Re-read only the map and the hero's position from the save (after plan_location in the MCP process).
    public void ReloadMapFrom(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        if (root == null) return;
        var map = root.FirstOrDefault(kv => kv.Key.Equals("map", StringComparison.OrdinalIgnoreCase)).Value;
        var hero = root.FirstOrDefault(kv => kv.Key.Equals("hero", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
        var pos = hero?.FirstOrDefault(kv => kv.Key.Equals("position", StringComparison.OrdinalIgnoreCase)).Value;
        if (map == null) return;
        GameWorld.ClearMap(WorldState.Map);
        var patch = new JsonObject { ["map"] = map.DeepClone() };
        if (pos != null) patch["hero"] = new JsonObject { ["position"] = pos.DeepClone() };
        ApplyUpdateWorldState(patch.ToJsonString());
        _entityColors.Clear();
    }

    public void LoadFrom(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        WorldState.Hero = null;
        WorldState.Narrative = null;
        WorldState.Map.Rooms = null;
        WorldState.Map.Doors = null;
        WorldState.Map.Area = null;
        WorldState.Map.Entities = null;
        WorldState.Map.Objects = null;
        WorldState.Map.TriggerCells = null;
        WorldState.Map.Chunks = null;
        WorldState.Map.Stairs = null;
        WorldState.Map.Furniture = null;
        WorldState.Images = null;
        WorldState.ActiveImageKey = null;
        WorldState.World = null;
        WorldState.PendingTravel = null;
        // The save is applied as a patch, and its elements have no ids — PatchByIndex would append them
        // to those already loaded: loading again (Menu → Continue) doubled scheduledEvents.
        WorldState.ScheduledEvents = null;
        WorldState.Combat = null;
        WorldState?.History?.Clear();
        _entityColors.Clear();
        ApplySavedState(json);
        LoadExploredCellsFrom(path);
    }
}

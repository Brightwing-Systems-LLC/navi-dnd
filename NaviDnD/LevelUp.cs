using System.Text.Json;
using System.Text.Json.Nodes;
using NaviDnD.Clients;
using NaviDnD.Data.Models;
using NaviDnD.Helpers;

namespace NaviDnD;

// Levelling up (the "New level" window). The player does not edit the hero freely — they only choose from what is offered:
//   the code — level, proficiency bonus, Hit Points (average or a Hit Die roll), Ability Score Improvement;
//   the DM (LevelUpOptions) — what the class gains at this level and the choice groups (subclass, spells, feats…);
//   the DM (LevelUpApply) — a hero patch for what was chosen; the code lets through only the hero's sheets (stats, skills, abilities,
//   resources, spells) and puts its own numbers on top. Everything happens on a copy of the hero: "Accept" replaces the hero entirely.
public sealed class LevelUp
{
    public sealed class Option
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        // An option the player asked for (the input field) — the DM added it to the group; marked with "★".
        public bool Custom { get; set; }
        // A resource increase (slots, Ki, uses): the name as on the hero sheet and the new "current/max" — not an ability.
        public string? Resource { get; set; }
        // A stat change (Spells Prepared, Speed…): the name as on the hero sheet, the new value in Value.
        public string? Stat { get; set; }
        public string? Value { get; set; }
    }

    public sealed class Choice
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int Count { get; set; } = 1;
        public List<Option> Options { get; set; } = [];
        [System.Text.Json.Serialization.JsonIgnore] public HashSet<int> Picked { get; } = [];

        // How many must be picked: if there are fewer options than required — all of them.
        [System.Text.Json.Serialization.JsonIgnore] public int Needed => Math.Min(Count, Options.Count);
        [System.Text.Json.Serialization.JsonIgnore] public bool Done => Picked.Count == Needed;

        // Select/deselect an option; if enough are already picked, selecting one replaces it.
        public void Toggle(int index)
        {
            if (index < 0 || index >= Options.Count) return;
            if (Picked.Remove(index)) return;
            if (Picked.Count >= Needed)
            {
                if (Needed != 1) return;
                Picked.Clear();
            }
            Picked.Add(index);
        }
    }

    // Ability Score Improvement: +2 to one, +1 to two, or a feat (the DM's group with id "feat").
    public enum AsiMode { None, Plus2, Plus1x2, Feat }

    public const string FeatId = "feat";

    public Hero Hero { get; }
    public LevelRules Rules { get; }
    public int From { get; }
    public int To => From + 1;
    public int HitDie { get; }
    public bool IsAsi { get; }

    public List<Option> Gains { get; private set; } = [];
    public List<Choice> Choices { get; private set; } = [];
    public bool PlanLoaded { get; private set; }

    // Hit Points: null — not chosen; Roll — the die value after rolling (one roll, no rerolls).
    public bool? HpRoll { get; private set; }
    public int? HpDie { get; private set; }

    public AsiMode Asi { get; set; } = AsiMode.None;
    public List<string> AsiPicks { get; } = [];

    // The window opens when: there is enough XP, the hero is alive and not in combat.
    public static bool Available(WorldState ws) =>
        ws.Hero is { Dead: not true } hero && ws.Combat?.Active != true && LevelRules.Current.CanLevelUp(hero);

    public LevelUp(Hero hero, LevelRules rules)
    {
        Hero = hero;
        Rules = rules;
        From = LevelRules.LevelOf(hero);
        HitDie = rules.HitDie(hero);
        IsAsi = rules.IsAsiLevel(hero, To);
        // A level-up to this same level started earlier — options and the roll come from it, no new request.
        if (hero.LevelUpDraft is { } draft && draft.Level == To)
        {
            if (draft.Plan != null) LoadPlan(draft.Plan);
            if (draft.HpDie is int die) { HpRoll = true; HpDie = Math.Clamp(die, 1, HitDie); }
        }
    }

    private static readonly JsonSerializerOptions DraftJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    // Options and the roll are stored in the hero (saved with the game): after cancelling or restarting, the window resumes from them.
    public void SaveDraft() => Hero.LevelUpDraft = new LevelUpDraft
    {
        Level = To,
        Plan = PlanLoaded ? JsonSerializer.Serialize(new { gains = Gains, choices = Choices }, DraftJson) : null,
        HpDie = HpDie,
    };

    public int Score(string ability) => AbilityNames.Score(AbilityNames.Find(Hero, ability)) ?? 10;

    // The new ability score, taking the chosen improvement into account.
    public int NewScore(string ability) =>
        Math.Min(Rules.MaxScore, Score(ability) + (Asi == AsiMode.Plus2 ? 2 : Asi == AsiMode.Plus1x2 ? 1 : 0) * AsiPicks.Count(a => a == ability));

    private int ConMod => AbilityNames.Modifier(Score(AbilityNames.Con));

    public int HpAverage => Math.Max(1, HitDie / 2 + 1 + ConMod);

    // Hit Point gain: the die (average or roll) + Constitution; if the Constitution modifier went up — plus the difference
    // for every level (5e rule).
    public int HpGain
    {
        get
        {
            int die = HpRoll == true ? Math.Max(1, (HpDie ?? 1) + ConMod) : HpAverage;
            int conDelta = AbilityNames.Modifier(NewScore(AbilityNames.Con)) - ConMod;
            return die + conDelta * To;
        }
    }

    public void ChooseAverage()
    {
        if (HpRoll == true) return;   // the roll is made — no rerolls
        HpRoll = false;
    }

    public int RollHp(Random random)
    {
        if (HpRoll == true) return HpDie!.Value;
        HpRoll = true;
        HpDie = random.Next(1, HitDie + 1);
        return HpDie.Value;
    }

    public void SetAsi(AsiMode mode)
    {
        if (mode == Asi) return;
        Asi = mode;
        AsiPicks.Clear();
        if (Choices.FirstOrDefault(c => c.Id == FeatId) is { } feat) feat.Picked.Clear();
    }

    // Picking an ability to improve: +2 — one (replaces), +1/+1 — two different ones; not allowed at the maximum.
    public void ToggleAsi(string ability)
    {
        if (AsiPicks.Remove(ability)) return;
        if (Score(ability) >= Rules.MaxScore) return;
        int needed = Asi == AsiMode.Plus2 ? 1 : Asi == AsiMode.Plus1x2 ? 2 : 0;
        if (needed == 0) return;
        if (AsiPicks.Count >= needed)
        {
            if (needed != 1) return;
            AsiPicks.Clear();
        }
        AsiPicks.Add(ability);
    }

    // Choice groups visible right now: feats only if a feat was chosen instead of the improvement.
    public IEnumerable<Choice> VisibleChoices => Choices.Where(c => c.Id != FeatId || Asi == AsiMode.Feat);

    public bool HasFeats => Choices.Any(c => c.Id == FeatId && c.Options.Count > 0);

    public bool AsiDone => !IsAsi || Asi switch
    {
        AsiMode.Plus2 => AsiPicks.Count == 1,
        AsiMode.Plus1x2 => AsiPicks.Count == 2,
        AsiMode.Feat => true,
        _ => false,
    };

    public bool Ready => PlanLoaded && HpRoll != null && AsiDone && VisibleChoices.All(c => c.Done);

    // Whether there is something to put on the hero sheet besides the engine's numbers: abilities, choices, recalculation from the proficiency bonus or ability scores.
    public bool NeedsMaster => Gains.Count > 0 || VisibleChoices.Any() || AsiPicks.Count > 0
                               || Rules.ProficiencyAt(To) != Rules.ProficiencyAt(From);

    // ── The DM's response with options ──────────────────────────────────────

    // {"gains":[{name,description}], "choices":[{id,title,count,options:[{name,description}]}]}. Empty groups
    // and duplicate feats (the code offers that choice itself) are dropped. false — could not be parsed.
    public bool LoadPlan(string json)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) return false;
            // What the hero already has (an ability from a previous level) is not "gained"; a resource increase is.
            var has = Hero.Abilities.Select(a => a.Name).Concat((Hero.Spells ?? []).Select(sp => sp.Name))
                .Select(n => n?.Trim().ToLowerInvariant()).ToHashSet();
            Gains = root["gains"]?.Deserialize<List<Option>>(options)?
                .Where(g => g.Name.Trim().Length > 0 && (g.Resource != null || g.Stat != null || !has.Contains(g.Name.Trim().ToLowerInvariant())))
                .ToList() ?? [];
            Choices = root["choices"]?.Deserialize<List<Choice>>(options)?
                .Where(c => c.Options is { Count: > 0 } && c.Count > 0)
                .ToList() ?? [];
            // Feats only at an Ability Score Improvement level.
            if (!IsAsi) Choices.RemoveAll(c => c.Id == FeatId);
            for (int i = 0; i < Choices.Count; i++)
                if (string.IsNullOrWhiteSpace(Choices[i].Id)) Choices[i].Id = $"choice{i}";
            PlanLoaded = true;
            return true;
        }
        catch { return false; }
    }

    // ── Requests to the DM ──────────────────────────────────────────────────

    public string ClassName => ClassLevel.Split(Hero.Stat(StatKeys.Class)?.Value).cls;

    private string HeroSheet() => new AiContextBuilder(new WorldState { Hero = Hero })
        .Serialize("hero.stats", "hero.skills", "hero.resources", "hero.abilities", "hero.spells");

    public string OptionsRequest() =>
        $"Level up: {ClassName} {From} → {To}. Hit Points, proficiency bonus ({AbilityNames.Signed(Rules.ProficiencyAt(To))})"
        + (IsAsi ? " and Ability Score Improvement come from the engine; add a `feat` group with suitable feats (an alternative to the improvement)." : " come from the engine.")
        + $"\nHero:\n{HeroSheet()}";

    // What the player picked — to the DM for the patch.
    public string ApplyRequest()
    {
        var lines = new List<string>
        {
            $"Level up: {ClassName} {From} → {To}. Proficiency bonus: {AbilityNames.Signed(Rules.ProficiencyAt(From))} → {AbilityNames.Signed(Rules.ProficiencyAt(To))}.",
        };
        if (Gains.Count > 0) lines.Add("Gained: " + string.Join("; ", Gains.Select(g => $"{g.Name} — {g.Description}")));
        foreach (var c in VisibleChoices)
            lines.Add($"Picked ({c.Title}): " + string.Join("; ", c.Picked.Order().Select(i => $"{c.Options[i].Name} — {c.Options[i].Description}")));
        if (Asi is AsiMode.Plus2 or AsiMode.Plus1x2)
            lines.Add("Ability Score Improvement (the engine sets the values): " + string.Join(", ", AsiPicks.Distinct()
                .Select(a => $"{AbilityNames.Find(Hero, a)?.Name ?? a} {Score(a)} → {NewScore(a)}")));
        lines.Add($"Hero (before levelling up):\n{HeroSheet()}");
        return string.Join("\n", lines);
    }

    // ── Result ──────────────────────────────────────────────────────────────

    private static readonly string[] AllowedHeroKeys = ["stats", "skills", "abilities", "resources", "spells", "actions", "speedmax"];

    // The new hero: a copy of the current one + the allowed part of the DM's patch + the engine's numbers. The original hero is unchanged.
    public Hero Apply(string? masterPatchJson)
    {
        var scratch = new Storage();
        // Applying the patch sets the world language from its state (L.SetWorld) — the copy's is the same as the game's.
        scratch.WorldState.Language = L.World;
        scratch.WorldState.Hero =JsonSerializer.Deserialize<Hero>(JsonSerializer.Serialize(Hero, CopyJson), CopyJson);
        if (masterPatchJson != null && JsonNode.Parse(masterPatchJson) is JsonObject root
            && root.FirstOrDefault(p => p.Key.Equals("hero", StringComparison.OrdinalIgnoreCase)).Value is JsonObject patch)
        {
            var allowed = new JsonObject();
            foreach (var (key, value) in patch.ToList())
                if (AllowedHeroKeys.Contains(key.ToLowerInvariant())) allowed[key] = value?.DeepClone();
            scratch.ApplyUpdateWorldState(new JsonObject { ["hero"] = allowed }.ToJsonString());
        }

        var hero = scratch.WorldState.Hero!;
        // An ability/spell colour the DM did not send defaults to "utility", like the others (otherwise the line stands out).
        foreach (var ability in hero.Abilities.Where(x => x.Color is not { Count: 3 })) ability.Color = [.. UtilityColor];
        foreach (var spell in (hero.Spells ?? []).Where(x => x.Color is not { Count: 3 })) spell.Color = [.. UtilityColor];
        foreach (string ability in AbilityNames.All)
            if (NewScore(ability) != Score(ability) && AbilityNames.Find(hero, ability) is { } stat)
                stat.Value = AbilityNames.Format(NewScore(ability));
        if (AbilityNames.Proficiency(hero) is { } prof) prof.Value = AbilityNames.Signed(Rules.ProficiencyAt(To));
        int gain = HpGain;
        if (HpRoll != null && hero.Hp?.Split('/') is [var cur, var max] && int.TryParse(cur.Trim(), out int c) && int.TryParse(max.Trim(), out int m))
            hero.Hp = $"{c + gain}/{m + gain}";
        hero.Level = To;
        hero.Xp = Hero.Xp;
        hero.LevelUpDraft = null;   // the level is gained — the draft is no longer needed
        LimitPrepared(hero);
        return hero;
    }

    // Prepared spells (not cantrips) — no more than the class maximum: the extra ones are the new ones (learned at
    // this level); the player prepares them. "Known" classes (without the "Prepared" stat) are left alone.
    private void LimitPrepared(Hero hero)
    {
        if (!int.TryParse(hero.Stat(StatKeys.SpellsPrepared)?.Value?.Trim(), out int max)) return;
        var old = (Hero.Spells ?? []).Where(sp => sp.Deleted != true).Select(sp => sp.Name).ToHashSet();
        var prepared = (hero.Spells ?? []).Where(sp => sp.Deleted != true && sp.Level > 0 && sp.Prepared == true)
            .OrderBy(sp => old.Contains(sp.Name) ? 0 : 1).ToList();
        foreach (var sp in prepared.Skip(max)) sp.Prepared = false;
    }

    // Preview for the hero card before "Accept" — without the AI: the engine's numbers, gains and picks as abilities,
    // reference spells as spells (the DM recalculates skills and saving throws on "Accept").
    public Hero Preview()
    {
        var hero = Apply(null);
        var known = hero.Abilities.Select(a => a.Name).Concat((hero.Spells ?? []).Select(sp => sp.Name)).ToHashSet();
        foreach (var option in Gains.Concat(VisibleChoices.SelectMany(c => c.Picked.Order().Select(i => c.Options[i]))))
        {
            // A stat change — the new value in its row (by name or key), not an ability.
            if (option.Stat != null)
            {
                if (option.Value == null) continue;
                string? key = StatKeys.FromName(option.Stat);
                var stat = hero.Stats.FirstOrDefault(x => x.Deleted != true
                    && (string.Equals(x.Name?.Trim(), option.Stat.Trim(), StringComparison.OrdinalIgnoreCase) || key != null && x.Key == key));
                if (stat != null) stat.Value = option.Value;
                else hero.Stats.Add(new HeroStat { Name = option.Stat, Value = option.Value, Key = key });
                continue;
            }
            // A resource increase — the new value in its row (a new resource — a new row), not an ability.
            string resName = option.Resource ?? option.Name;
            if (option.Resource != null || hero.Resources.Any(r => r.Deleted != true && r.Name == resName))
            {
                if (option.Value == null) continue;
                if (hero.Resources.FirstOrDefault(r => r.Deleted != true && r.Name == resName) is { } res) res.Value = option.Value;
                else hero.Resources.Add(new HeroResource { Name = resName, Value = option.Value,
                    Category = resName.Contains("ячейк", StringComparison.OrdinalIgnoreCase) || resName.Contains("slot", StringComparison.OrdinalIgnoreCase)
                        ? ResourceCategories.Spells : ResourceCategories.Abilities });
                continue;
            }
            if (!known.Add(option.Name)) continue;
            if (SpellDatabase.Find(option.Name) is { } spell)
                (hero.Spells ??= []).Add(new HeroSpell { Name = option.Name, Level = spell.Level, Prepared = true, Color = [.. UtilityColor] });
            else
                hero.Abilities.Add(new HeroAbility { Name = option.Name, Description = option.Description, Color = [.. UtilityColor] });
        }
        LimitPrepared(hero);
        return hero;
    }

    // ── A custom option (player request) ────────────────────────────────────

    public string CustomRequest(string request) => OptionsRequest()
        + $"\nPlayer request: {request.Trim()}\nChoices at this level: "
        + string.Join("; ", Choices.Select(c => $"{c.Id} — {c.Title} ({string.Join(", ", c.Options.Select(o => o.Name))})"));

    // The DM's answer to the request: {"choices":[{id,title,count,options:[…]}]} — options are appended to the group with the same id
    // (a new group — whole), or {"refusal":"…"}. Returns the refusal text; null — the option was added.
    public string? MergeCustom(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root?["refusal"]?.GetValue<string>() is { Length: > 0 } refusal) return refusal;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var groups = root?["choices"]?.Deserialize<List<Choice>>(options)?.Where(c => c.Options is { Count: > 0 }).ToList() ?? [];
            if (groups.Count == 0) return L.T("The DM did not offer an option.");
            foreach (var g in groups)
            {
                foreach (var o in g.Options) o.Custom = true;
                if (Choices.FirstOrDefault(c => c.Id == g.Id) is { } existing)
                    existing.Options.AddRange(g.Options.Where(o => existing.Options.All(e => e.Name != o.Name)));
                else if (g.Id != FeatId || IsAsi)
                {
                    if (string.IsNullOrWhiteSpace(g.Id)) g.Id = $"custom{Choices.Count}";
                    if (g.Count <= 0) g.Count = 1;
                    Choices.Add(g);
                }
            }
            return null;
        }
        catch { return L.T("The DM did not answer — try again."); }
    }

    private static readonly int[] UtilityColor = [220, 220, 220];

    private static readonly JsonSerializerOptions CopyJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

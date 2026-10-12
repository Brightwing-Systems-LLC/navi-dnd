using NaviDnD.Data.Models;
using NaviDnD.Helpers;

namespace NaviDnD.Tests;

// Experience and levels: the engine owns hero.level/xp (the DM only sends xpAward); the "New level" window builds the hero
// from the player's choices and the DM's patch (LevelUp) — without the model.
public class LevelUpTests
{
    private static Hero Fighter(int level = 3, int xp = 2700) => new()
    {
        Symbol = "HRO",
        Name = "Kadrim",
        Hp = "20/28",
        Level = level,
        Xp = xp,
        Stats =
        [
            new() { Name = "Strength", Value = "+3 (16)" },
            new() { Name = "Constitution", Value = "+2 (15)" },
            new() { Name = "Proficiency Bonus", Value = "+2" },
            new() { Name = "Class", Value = "Fighter", Key = StatKeys.Class },
        ],
        Skills = [new() { Name = "Athletics", Value = "+5" }],
        Inventory = [new() { Name = "Sword", Description = "Longsword" }],
        Abilities = [new() { Name = "Second Wind", Description = "1d10+level" }],
        Resources = [],
        EquipmentSlots = [],
    };

    private static Storage WithHero(Hero hero)
    {
        var storage = new Storage();
        storage.WorldState.Hero = hero;
        return storage;
    }

    [Theory]
    [InlineData("Воин 1 ур", "Воин", 1)]
    [InlineData("Воин (Чемпион) 3 ур.", "Воин (Чемпион)", 3)]
    [InlineData("Wizard (level 2)", "Wizard", 2)]
    [InlineData("Druid 1 lvl", "Druid", 1)]
    [InlineData("Fighter 12", "Fighter", 12)]
    public void ClassLevelSplitsLevelOff(string value, string cls, int level)
    {
        Assert.Equal((cls, (int?)level), ClassLevel.Split(value));
    }

    [Fact]
    public void ClassWithoutLevelStaysAsIs()
    {
        Assert.Equal(("Fighter (Champion)", (int?)null), ClassLevel.Split("Fighter (Champion)"));
    }

    [Fact]
    public void MasterCannotPatchLevelOrXp()
    {
        var storage = WithHero(Fighter());
        storage.ApplyUpdateWorldState("""{"hero":{"level":10,"xp":99999}}""");
        Assert.Equal(3, storage.WorldState.Hero!.Level);
        Assert.Equal(2700, storage.WorldState.Hero.Xp);
    }

    [Fact]
    public void SavedStateKeepsLevelAndXp()
    {
        var storage = new Storage();
        storage.ApplySavedState("""{"hero":{"name":"Kadrim","level":5,"xp":7000}}""");
        Assert.Equal(5, storage.WorldState.Hero!.Level);
        Assert.Equal(7000, storage.WorldState.Hero.Xp);
    }

    [Fact]
    public void XpAwardCountsMonstersByChallengeRating()
    {
        var storage = WithHero(Fighter(xp: 2600));
        storage.ApplyUpdateWorldState("""{"hero":{"xpAward":{"monsters":["Гоблин","Goblin","Unknown creature"],"xp":25,"reason":"ambush at the bridge"}}}""");
        Assert.Equal(2600 + 50 + 50 + 25, storage.WorldState.Hero!.Xp);
        var notices = storage.TakeNotices();
        Assert.Equal(L.WF("[+{0} XP: {1}]", 125, "ambush at the bridge"), notices[0]);
        Assert.Equal(2, notices.Count);   // the 4th-level threshold (2700) is crossed
        Assert.Empty(storage.TakeNotices());
    }

    [Fact]
    public void ZeroXpAwardAddsNothing()
    {
        var storage = WithHero(Fighter());
        storage.ApplyUpdateWorldState("""{"hero":{"xpAward":{"monsters":["Unknown creature"]}}}""");
        Assert.Equal(2700, storage.WorldState.Hero!.Xp);
        Assert.Empty(storage.TakeNotices());
    }

    [Fact]
    public void NewHeroTakesLevelFromClassStat()
    {
        var storage = new Storage();
        storage.ApplyUpdateWorldState("""{"hero":{"name":"Lyra","stats":[{"name":"Class","value":"Wizard 2 lvl"}]}}""");
        var hero = storage.WorldState.Hero!;
        Assert.Equal(2, hero.Level);
        Assert.Equal(300, hero.Xp);
        Assert.Equal("Wizard", hero.Stat(StatKeys.Class)!.Value);
    }

    [Fact]
    public void ClassStatWithLevelDoesNotRaiseLevel()
    {
        var storage = WithHero(Fighter());
        storage.ApplyUpdateWorldState("""{"hero":{"stats":[{"id":3,"value":"Fighter 9 lvl"}]}}""");
        Assert.Equal(3, storage.WorldState.Hero!.Level);
        Assert.Equal("Fighter", storage.WorldState.Hero.Stat(StatKeys.Class)!.Value);
    }

    [Fact]
    public void RulesFollowTheTable()
    {
        var rules = LevelRules.Current;
        Assert.Equal(20, rules.MaxLevel);
        Assert.Equal(2700, rules.NextXp(3));
        Assert.True(rules.CanLevelUp(Fighter(xp: 2700)));
        Assert.False(rules.CanLevelUp(Fighter(xp: 2699)));
        Assert.Equal(10, rules.HitDie(Fighter()));
        Assert.True(rules.IsAsiLevel(Fighter(), 6));         // fighters get an extra improvement at 6th
        Assert.Equal(3, rules.ProficiencyAt(5));
    }

    [Fact]
    public void NotAvailableInCombat()
    {
        var ws = new WorldState { Hero = Fighter(), Combat = new CombatState { Active = true } };
        Assert.False(LevelUp.Available(ws));
        ws.Combat = null;
        Assert.True(LevelUp.Available(ws));
    }

    [Fact]
    public void ReadyOnlyWhenEverythingChosen()
    {
        var up = new LevelUp(Fighter(), LevelRules.Current);
        Assert.True(up.IsAsi);   // 3 → 4
        Assert.True(up.LoadPlan("""
            {"gains":[{"name":"Improved Maneuver","description":"…"}],
             "choices":[{"id":"style","title":"Style","count":1,"options":[{"name":"Defense","description":"+1 AC"},{"name":"Dueling","description":"+2 damage"}]},
                        {"id":"feat","title":"Feat","count":1,"options":[{"name":"Tough","description":"…"}]}]}
            """));
        Assert.False(up.Ready);
        up.ChooseAverage();
        up.Choices[0].Toggle(1);
        up.SetAsi(LevelUp.AsiMode.Plus1x2);
        up.ToggleAsi(AbilityNames.Str);
        Assert.False(up.Ready);                              // two abilities are needed
        up.ToggleAsi(AbilityNames.Con);
        Assert.True(up.Ready);                               // the feat is hidden — not needed
        Assert.DoesNotContain(up.VisibleChoices, c => c.Id == LevelUp.FeatId);
        up.SetAsi(LevelUp.AsiMode.Feat);
        Assert.False(up.Ready);
        up.Choices.Single(c => c.Id == LevelUp.FeatId).Toggle(0);
        Assert.True(up.Ready);
    }

    [Fact]
    public void SingleChoiceReplacesPick()
    {
        var c = new LevelUp.Choice { Count = 1, Options = [new() { Name = "A" }, new() { Name = "B" }] };
        c.Toggle(0);
        c.Toggle(1);
        Assert.Equal([1], c.Picked);
    }

    [Fact]
    public void HitPointsRollOnce()
    {
        var up = new LevelUp(Fighter(), LevelRules.Current);
        Assert.Equal(10 / 2 + 1 + 2, up.HpAverage);
        int first = up.RollHp(new Random(1));
        Assert.Equal(first, up.RollHp(new Random(2)));       // no reroll
        up.ChooseAverage();
        Assert.True(up.HpRoll);                              // after rolling, can't switch to the average
    }

    [Fact]
    public void ApplyKeepsOnlySheetChangesAndSetsEngineNumbers()
    {
        var hero = Fighter();
        var up = new LevelUp(hero, LevelRules.Current);
        up.LoadPlan("""{"gains":[],"choices":[]}""");
        up.ChooseAverage();
        up.SetAsi(LevelUp.AsiMode.Plus1x2);
        up.ToggleAsi(AbilityNames.Str);
        up.ToggleAsi(AbilityNames.Con);                       // 15 → 16: modifier +2 → +3

        var after = up.Apply("""
            {"hero":{"abilities":[{"name":"Toughness","description":"…"}],"skills":[{"id":0,"value":"+6"}],
                     "inventory":[{"id":0,"deleted":true}],"hp":"999/999","level":20,"xp":1}}
            """);

        Assert.Equal(4, after.Level);
        Assert.Equal(2700, after.Xp);
        // Average d10 with the old Con: 6+2; the Con modifier rose by 1 — another +1 for each of the 4 levels.
        Assert.Equal(8 + 4, up.HpGain);
        Assert.Equal("32/40", after.Hp);
        Assert.Equal("+3 (17)", AbilityNames.Find(after, AbilityNames.Str)!.Value);
        Assert.Equal("+3 (16)", AbilityNames.Find(after, AbilityNames.Con)!.Value);
        Assert.Equal("+6", after.Skills[0].Value);
        Assert.Contains(after.Abilities, a => a.Name == "Toughness" && a.Color is [220, 220, 220]);   // no color from the DM — utility
        Assert.Single(after.Inventory);                       // the DM doesn't touch the inventory here
        // The original hero is unchanged until "Accept".
        Assert.Equal(3, hero.Level);
        Assert.Equal("+3 (16)", hero.Stats[0].Value);
        Assert.Single(hero.Abilities);
    }

    [Fact]
    public void ApplyRequestListsChosenOptions()
    {
        var up = new LevelUp(Fighter(level: 2, xp: 900), LevelRules.Current);
        up.LoadPlan("""{"gains":[],"choices":[{"id":"subclass","title":"Archetype","count":1,"options":[{"name":"Champion","description":"Crit 19–20"},{"name":"Battle Master","description":"Maneuvers"}]}]}""");
        up.Choices[0].Toggle(0);
        string request = up.ApplyRequest();
        Assert.Contains("Fighter 2 → 3", request);
        Assert.Contains("Champion — Crit 19–20", request);
        Assert.DoesNotContain("Battle Master", request);
        Assert.False(up.IsAsi);
    }

    [Fact]
    public void KnownAbilityIsNotAGainAndResourceGrowthShowsInPreview()
    {
        var hero = Fighter();
        hero.Resources = [new() { Name = "1st-level slots", Value = "2/2", Category = ResourceCategories.Spells }];
        var up = new LevelUp(hero, LevelRules.Current);
        up.LoadPlan("""
            {"gains":[{"name":"Second Wind","description":"already known"},
                      {"name":"1st-level slots","description":"3 instead of 2","resource":"1st-level slots","value":"3/3"}]}
            """);
        Assert.Equal(["1st-level slots"], up.Gains.Select(g => g.Name));
        var preview = up.Preview();
        Assert.Equal("3/3", preview.Resources.Single().Value);
        Assert.DoesNotContain(preview.Abilities, a => a.Name == "1st-level slots");
        Assert.Equal("2/2", hero.Resources.Single().Value);
    }

    [Fact]
    public void DraftSurvivesSaveAndReload()
    {
        var hero = Fighter(level: 2, xp: 900);
        var up = new LevelUp(hero, LevelRules.Current);
        up.LoadPlan("""{"choices":[{"id":"subclass","title":"Archetype","count":1,"options":[{"name":"Champion"}]}]}""");
        up.MergeCustom("""{"choices":[{"id":"subclass","title":"Archetype","count":1,"options":[{"name":"Eldritch Knight"}]}]}""");
        int die = up.RollHp(new Random(3));
        up.SaveDraft();

        // Save → load: the draft is still there, and the DM's patch doesn't change it.
        var saved = System.Text.Json.JsonSerializer.Serialize(new { hero },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var storage = new Storage();
        storage.ApplySavedState(saved);
        storage.ApplyUpdateWorldState("""{"hero":{"levelUpDraft":null}}""");
        var again = new LevelUp(storage.WorldState.Hero!, LevelRules.Current);

        Assert.True(again.PlanLoaded);
        Assert.Contains(again.Choices[0].Options, o => o.Name == "Eldritch Knight" && o.Custom);
        Assert.Equal(die, again.HpDie);
        Assert.True(again.HpRoll);
        Assert.Null(again.Apply(null).LevelUpDraft);   // the level is gained — the draft is no longer needed
    }

    [Fact]
    public void ApplyKeepsWorldLanguage()
    {
        string before = L.World;
        try
        {
            L.SetWorld(L.Russian);
            new LevelUp(Fighter(), LevelRules.Current).Apply("""{"hero":{"abilities":[{"name":"Action Surge"}]}}""");
            Assert.Equal(L.Russian, L.World);
        }
        finally { L.SetWorld(before); }
    }

    [Fact]
    public void NewSpellsDoNotExceedPreparedMax()
    {
        var hero = Fighter();
        hero.Stats.Add(new() { Name = "Spells prepared", Value = "2", Key = StatKeys.SpellsPrepared });
        hero.Spells = [new() { Name = "Shield", Level = 1, Prepared = true }, new() { Name = "Sleep", Level = 1, Prepared = true }];
        var up = new LevelUp(hero, LevelRules.Current);
        up.ChooseAverage();
        var after = up.Apply("""{"hero":{"spells":[{"name":"Disguise Self","level":1,"prepared":true}]}}""");
        Assert.Equal(2, after.Spells!.Count(sp => sp.Prepared == true));
        Assert.False(after.Spells!.Single(sp => sp.Name == "Disguise Self").Prepared);
    }

    [Fact]
    public void StatGrowthShowsInPreview()
    {
        var hero = Fighter();
        hero.Stats.Add(new() { Name = "Spells prepared", Value = "4", Key = StatKeys.SpellsPrepared });
        var up = new LevelUp(hero, LevelRules.Current);
        up.LoadPlan("""{"gains":[{"name":"Spell Preparation","description":"5 instead of 4","stat":"Spells prepared","value":"5"}]}""");
        var preview = up.Preview();
        Assert.Equal("5", preview.Stat(StatKeys.SpellsPrepared)!.Value);
        Assert.DoesNotContain(preview.Abilities, a => a.Name == "Spell Preparation");
        Assert.Equal("4", hero.Stat(StatKeys.SpellsPrepared)!.Value);
    }

    [Fact]
    public void CustomOptionJoinsItsGroupOrRefuses()
    {
        var up = new LevelUp(Fighter(level: 2, xp: 900), LevelRules.Current);
        up.LoadPlan("""{"choices":[{"id":"subclass","title":"Archetype","count":1,"options":[{"name":"Champion"}]}]}""");
        Assert.Null(up.MergeCustom("""{"choices":[{"id":"subclass","title":"Archetype","count":1,"options":[{"name":"Eldritch Knight"}]}]}"""));
        Assert.Contains(up.Choices[0].Options, o => o.Name == "Eldritch Knight" && o.Custom);
        Assert.Equal("Rules only", up.MergeCustom("""{"refusal":"Rules only"}"""));
    }

    [Fact]
    public void FeatsDroppedOffAsiLevels()
    {
        var up = new LevelUp(Fighter(level: 1, xp: 300), LevelRules.Current);
        up.LoadPlan("""{"choices":[{"id":"feat","title":"Feat","count":1,"options":[{"name":"Tough"}]}]}""");
        Assert.Empty(up.Choices);
    }

    [Fact]
    public void ApplyAddsSpellsAndAverageHp()
    {
        var up = new LevelUp(Fighter(), LevelRules.Current);
        up.ChooseAverage();
        var after = up.Apply("""{"hero":{"spells":[{"name":"Shield","level":1,"prepared":true}]}}""");
        Assert.Contains(after.Spells!, sp => sp.Name == "Shield");
        Assert.Equal("28/36", after.Hp);
    }
}

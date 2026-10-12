using NaviDnD;
using NaviDnD.Data.Models;

namespace NaviDnD.Tests;

public class StorageApplyUpdateTests
{
    private static Storage MakeStorage()
    {
        var s = new Storage();
        s.WorldState.Hero = new Hero
        {
            Symbol = "HRO",
            Name = "Hero",
            Position = [1, 1],
            Inventory = [],
            Abilities = [],
            Stats = [],
            Skills = [],
            Resources = [],
            EquipmentSlots = []
        };
        return s;
    }

    // ── Hero scalar fields ────────────────────────────────────────────────────

    [Fact]
    public void UpdatesHeroHp()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""{"hero":{"hp":"8/20"}}""");
        Assert.Equal("8/20", storage.WorldState.Hero!.Hp);
    }

    [Fact]
    public void UpdatesHeroName()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""{"hero":{"name":"Aragorn"}}""");
        Assert.Equal("Aragorn", storage.WorldState.Hero!.Name);
    }

    [Fact]
    public void UpdatesHeroSpeedLeft()
    {
        var storage = MakeStorage();
        storage.WorldState.Hero!.SpeedMax = 30;
        storage.ApplyUpdateWorldState("""{"hero":{"speedLeft":15}}""");
        Assert.Equal(15, storage.WorldState.Hero!.SpeedLeft);
    }

    // ── Time ─────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdatesTotalRounds()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""{"time":{"totalRounds":42}}""");
        Assert.Equal(42, storage.WorldState.Time.TotalRounds);
    }

    [Fact]
    public void UpdatesPartOfDay()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""{"time":{"partOfDay":"Twilight"}}""");
        Assert.Equal("Twilight", storage.WorldState.Time.PartOfDay);
    }

    // ── History ───────────────────────────────────────────────────────────────

    [Fact]
    public void AppendsHistory()
    {
        var storage = MakeStorage();
        storage.WorldState.History = [new DialogMessage { Text = "Beginning" }];
        storage.ApplyUpdateWorldState("""{"history":[{"text":"Continuation"}]}""");
        Assert.Equal(2, storage.WorldState.History.Count);
        Assert.Equal("Continuation", storage.WorldState.History[1].Text);
    }

    // ── PatchByIndex — Inventory ──────────────────────────────────────────────

    [Fact]
    public void PatchByIndex_AddsNewItem_WhenNoId()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""
            {"hero":{"inventory":[{"name":"Sword","description":"Longsword","quantity":1,"color":[200,200,200]}]}}
            """);
        Assert.Single(storage.WorldState.Hero!.Inventory);
        Assert.Equal("Sword", storage.WorldState.Hero.Inventory[0].Name);
    }

    [Fact]
    public void PatchByIndex_UpdatesExistingItem_WithId()
    {
        var storage = MakeStorage();
        storage.WorldState.Hero!.Inventory =
        [
            new HeroInventory { Name = "Sword", Description = "Standard", Quantity = 1, Color = [200, 200, 200] }
        ];

        storage.ApplyUpdateWorldState("""{"hero":{"inventory":[{"id":0,"quantity":3}]}}""");

        Assert.Single(storage.WorldState.Hero.Inventory);
        Assert.Equal("Sword", storage.WorldState.Hero.Inventory[0].Name); // name preserved
        Assert.Equal(3, storage.WorldState.Hero.Inventory[0].Quantity);  // quantity updated
    }

    [Fact]
    public void PatchByIndex_DeletesItem_WithDeletedTrue()
    {
        var storage = MakeStorage();
        storage.WorldState.Hero!.Inventory =
        [
            new HeroInventory { Name = "Sword", Description = "Longsword", Quantity = 1, Color = [200, 200, 200] },
            new HeroInventory { Name = "Shield", Description = "Wooden", Quantity = 1, Color = [150, 100, 50] }
        ];

        storage.ApplyUpdateWorldState("""{"hero":{"inventory":[{"id":0,"deleted":true}]}}""");

        Assert.Single(storage.WorldState.Hero.Inventory);
        Assert.Equal("Shield", storage.WorldState.Hero.Inventory[0].Name);
    }

    [Fact]
    public void PatchByIndex_AppendsWhenIdGeCount()
    {
        var storage = MakeStorage();
        storage.WorldState.Hero!.Inventory =
        [
            new HeroInventory { Name = "Sword", Description = "d", Quantity = 1, Color = [200, 200, 200] }
        ];

        storage.ApplyUpdateWorldState("""
            {"hero":{"inventory":[{"id":99,"name":"Bow","description":"d","quantity":1,"color":[100,100,100]}]}}
            """);

        Assert.Equal(2, storage.WorldState.Hero.Inventory.Count);
        Assert.Equal("Bow", storage.WorldState.Hero.Inventory[1].Name);
    }

    // ── PatchByIndex — Effects ────────────────────────────────────────────────

    [Fact]
    public void PatchByIndex_AddsEffect()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""
            {"hero":{"effects":[{"name":"Poison","expiresAtRound":10}]}}
            """);
        Assert.Single(storage.WorldState.Hero!.Effects!);
        Assert.Equal("Poison", storage.WorldState.Hero.Effects![0].Name);
        Assert.Equal(10, storage.WorldState.Hero.Effects![0].ExpiresAtRound);
    }

    [Fact]
    public void PatchByIndex_UpdatesEffect_WithId()
    {
        var storage = MakeStorage();
        storage.WorldState.Hero!.Effects =
        [
            new StatusEffect { Name = "Poison", ExpiresAtRound = 10 }
        ];

        storage.ApplyUpdateWorldState("""{"hero":{"effects":[{"id":0,"expiresAtRound":15}]}}""");

        Assert.Single(storage.WorldState.Hero.Effects!);
        Assert.Equal("Poison", storage.WorldState.Hero.Effects![0].Name);
        Assert.Equal(15, storage.WorldState.Hero.Effects![0].ExpiresAtRound);
    }

    [Fact]
    public void PatchByIndex_DeletesEffect()
    {
        var storage = MakeStorage();
        storage.WorldState.Hero!.Effects =
        [
            new StatusEffect { Name = "Poison", ExpiresAtRound = 10 },
            new StatusEffect { Name = "Bless", ExpiresAtRound = 20 }
        ];

        storage.ApplyUpdateWorldState("""{"hero":{"effects":[{"id":0,"deleted":true}]}}""");

        Assert.Single(storage.WorldState.Hero.Effects!);
        Assert.Equal("Bless", storage.WorldState.Hero.Effects![0].Name);
    }

    // ── Nested effects (map.entities[i].effects, patched by id like hero.effects) ──

    [Fact]
    public void NestedEntityEffects_AddedById_WithoutClobberingOthers()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""
            {"map":{"entities":[{"symbol":"ORC","name":"Orc","position":[2,2],"effects":[{"name":"Patrolling"}]}]}}
            """);

        // add a second effect to the same (already-existing) entity by patching just "effects"
        storage.ApplyUpdateWorldState("""
            {"map":{"entities":[{"id":0,"effects":[{"name":"Burning","expiresAtRound":12}]}]}}
            """);

        var effects = storage.WorldState.Map.Entities![0].Effects!;
        Assert.Equal(2, effects.Count);
        Assert.Equal("Patrolling", effects[0].Name);
        Assert.Equal("Burning", effects[1].Name);
        Assert.Equal(12, effects[1].ExpiresAtRound);
    }

    [Fact]
    public void NestedEntityEffects_UpdatedAndDeletedById()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""
            {"map":{"entities":[{"symbol":"ORC","name":"Orc","position":[2,2],
              "effects":[{"name":"Poison","expiresAtRound":10},{"name":"Burning","expiresAtRound":12}]}]}}
            """);

        storage.ApplyUpdateWorldState("""
            {"map":{"entities":[{"id":0,"effects":[{"id":0,"expiresAtRound":15},{"id":1,"deleted":true}]}]}}
            """);

        var effects = storage.WorldState.Map.Entities![0].Effects!;
        Assert.Single(effects);
        Assert.Equal("Poison", effects[0].Name);
        Assert.Equal(15, effects[0].ExpiresAtRound);
    }

    // ── ScheduledEvents ───────────────────────────────────────────────────────

    [Fact]
    public void PatchByIndex_AddsScheduledEvent()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""
            {"scheduledEvents":[{"name":"Explosion","fireAtRound":15,"effect":"The bomb explodes"}]}
            """);
        Assert.Single(storage.WorldState.ScheduledEvents!);
        Assert.Equal("Explosion", storage.WorldState.ScheduledEvents![0].Name);
    }

    [Fact]
    public void ApplyUpdate_NullScheduledEvents_DoesNotCrash()
    {
        var storage = MakeStorage();
        // This was the crash bug: scheduledEvents: null in JSON
        storage.ApplyUpdateWorldState("""{"scheduledEvents":null}""");
        // should survive without exception, events unchanged
        Assert.Null(storage.WorldState.ScheduledEvents);
    }

    [Fact]
    public void PatchByIndex_DeletesScheduledEvent()
    {
        var storage = MakeStorage();
        storage.WorldState.ScheduledEvents =
        [
            new ScheduledEvent { Name = "Explosion", FireAtRound = 10, Effect = "Bomb" },
            new ScheduledEvent { Name = "Enemies", FireAtRound = 20, Effect = "Reinforcements" }
        ];

        storage.ApplyUpdateWorldState("""{"scheduledEvents":[{"id":0,"deleted":true}]}""");

        Assert.Single(storage.WorldState.ScheduledEvents!);
        Assert.Equal("Enemies", storage.WorldState.ScheduledEvents![0].Name);
    }

    // ── MergeInto — partial update preserves other fields ────────────────────

    [Fact]
    public void MergeInto_PreservesUnchangedFields()
    {
        var storage = MakeStorage();
        storage.WorldState.Hero!.Inventory =
        [
            new HeroInventory { Name = "Sword", Description = "Longsword", Quantity = 5, Color = [200, 200, 200] }
        ];

        // Update only quantity, description should survive
        storage.ApplyUpdateWorldState("""{"hero":{"inventory":[{"id":0,"quantity":2}]}}""");

        var item = storage.WorldState.Hero.Inventory[0];
        Assert.Equal("Sword", item.Name);
        Assert.Equal("Longsword", item.Description);
        Assert.Equal(2, item.Quantity);
    }

    // ── Narrative ─────────────────────────────────────────────────────────────

    [Fact]
    public void UpdatesNarrativeWorld()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""{"narrative":{"world":"Dark fantasy"}}""");
        Assert.Equal("Dark fantasy", storage.WorldState.Narrative!.World);
    }

    [Fact]
    public void AppendsPlotThreads()
    {
        var storage = MakeStorage();
        storage.WorldState.Narrative = new Narrative { PlotThreads = [] };
        storage.ApplyUpdateWorldState("""
            {"narrative":{"plotThreads":[{"name":"Conspiracy","status":"active","description":"A secret order"}]}}
            """);
        Assert.Single(storage.WorldState.Narrative.PlotThreads!);
        Assert.Equal("Conspiracy", storage.WorldState.Narrative.PlotThreads![0].Name);
    }

    // ── Map ───────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdatesMapColsAndRows()
    {
        var storage = MakeStorage();
        storage.ApplyUpdateWorldState("""{"map":{"cols":20,"rows":15}}""");
        Assert.Equal(20, storage.WorldState.Map.Cols);
        Assert.Equal(15, storage.WorldState.Map.Rows);
    }
}

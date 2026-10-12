using NaviDnD.Clients;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// REAL Claude calls — see the SendActionPromptTests class for the general rules (costly, slow,
// run rarely and deliberately). Checks the whole lifecycle of status effects (§ CONDITIONS in
// systemPrompt.md): the AI actually adds hero.effects in response to a free-form player action (not only on
// a trigger/in combat, like the other PromptTests), then correctly removes the effect — manually (Prone) or
// when its round expires (round-transition).
public class EffectsPromptTests
{
    private readonly ITestOutputHelper _output;

    public EffectsPromptTests(ITestOutputHelper output) => _output = output;

    // Lying prone (Prone) — an effect with no expiresAtRound/untilLongRest (removed only by an explicit
    // player action, see § CONDITIONS "Indefinite/until removed manually"). Two real calls in a row
    // on ONE storage: first the hero drops prone, then stands up — the effect must appear and
    // disappear in hero.effects, not merely be mentioned in the narrative.
    [Fact]
    public async Task Prone_AddedWhenHeroGoesProne_AndRemovedWhenHeroStandsUp()
    {
        var storage = PromptScenario.LoadUiTestFixture("full_game.json");
        Assert.True(storage.WorldState.Hero!.Effects is not { Count: > 0 },
            "Scenario precondition: the hero must start with no active effects.");

        var ctx = new AiContextBuilder(storage.WorldState);

        // Step 1: the hero deliberately drops prone to take cover from arrows.
        string fallMessage = ctx.MinimalState("I throw myself to the ground and lie prone to take cover from the incoming arrows");
        var fallResult = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, fallMessage);
        _output.WriteLine("Model response (dropping prone):\n" + fallResult.RawResponse);

        var fallParsed = PromptScenario.ApplyAndParse(storage, fallResult.RawResponse);
        Assert.True(fallParsed.HistoryTexts.Count > 0, "Dropping prone must be described in history.");

        var activeEffects = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).ToList() ?? [];
        Assert.True(activeEffects.Count > 0,
            "After deliberately dropping prone the hero must get an active effect in hero.effects (not just text in history).");
        var prone = activeEffects[0];
        _output.WriteLine($"Added effect: \"{prone.Name}\" color={(prone.Color != null ? string.Join(",", prone.Color) : "null")} " +
                           $"expiresAtRound={prone.ExpiresAtRound?.ToString() ?? "null"} untilLongRest={prone.UntilLongRest}");
        Assert.False(string.IsNullOrWhiteSpace(prone.Name), "The added effect must have a name.");
        Assert.True(prone.Color is { Count: 3 }, "The added effect must have color:[R,G,B] (required by the prompt).");

        // Step 2: the hero stands up — the effect must be removed.
        string standMessage = ctx.MinimalState("I get back on my feet");
        var standResult = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, standMessage);
        _output.WriteLine("Model response (standing up):\n" + standResult.RawResponse);

        var standParsed = PromptScenario.ApplyAndParse(storage, standResult.RawResponse);
        Assert.True(standParsed.HistoryTexts.Count > 0, "Standing up must be described in history.");

        var stillActive = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).ToList() ?? [];
        Assert.DoesNotContain(stillActive, e => e.Name == prone.Name);

        PromptScenario.RecordAsMock(fallResult.RawResponse, mockResponsesFolder: "ProneEffect", action: "SendAction");
    }

    // A short buff (a few rounds) with onExpire — we check exactly the part of the lifecycle
    // the Prone test doesn't touch: the engine detects that the round has expired and sends a
    // separate round-transition call, and the real AI must remove the effect in response.
    //
    // The effect is added directly via a patch (not through a free-form action) — on purpose: two attempts
    // to get it through narrative ("battle cry" / "an allied cleric says a prayer") were CORRECTLY
    // rejected by the AI, because the hero has no such ability and there's no ally on the map — that's not
    // a bug, it's § ABSOLUTE PROHIBITIONS working ("don't invent things for the player/scene"). Here the goal is
    // to check not "adding" but specifically removal when the round expires, so the source of the effect
    // doesn't matter for the test.
    [Fact]
    public async Task TemporaryBuff_WithRoundLimit_IsRemovedAfterItExpires()
    {
        var storage = PromptScenario.LoadUiTestFixture("full_game.json");
        int currentRound = storage.WorldState.Time.TotalRounds;
        int expiresAtRound = currentRound + 2;
        string effectPatch = """
            {"hero":{"effects":[{"name":"Blessing","description":"You feel a surge of confidence.","color":[80,150,220],
              "expiresAtRound":EXPIRES_AT_ROUND,"onExpire":{"effect":"The Blessing fades — describe it briefly, remove the effect."}}]}}
            """.Replace("EXPIRES_AT_ROUND", expiresAtRound.ToString());
        storage.ApplyUpdateWorldState(effectPatch);

        var buff = storage.WorldState.Hero!.Effects!.Single(e => e.Deleted != true);
        _output.WriteLine($"Effect seeded directly: \"{buff.Name}\" expiresAtRound={buff.ExpiresAtRound}.");

        // Fast-forward time past the effect's duration and run the engine's round check.
        storage.WorldState.Time.TotalRounds = expiresAtRound + 1;
        var bundle = storage.TakeRoundBundle();
        Assert.NotNull(bundle);
        Assert.Contains(bundle.ExpiredEffects, e => e.Effect.Name == "Blessing");

        var ctx = new AiContextBuilder(storage.WorldState);
        string roundMessage = ctx.RoundTransitionState(bundle);
        var expireResult = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, roundMessage);
        _output.WriteLine("Model response (effect expired):\n" + expireResult.RawResponse);

        var expireParsed = PromptScenario.ApplyAndParse(storage, expireResult.RawResponse);
        Assert.True(expireParsed.HistoryTexts.Count > 0, "The effect expiring must be described in history.");

        var stillActive = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).ToList() ?? [];
        Assert.DoesNotContain(stillActive, e => e.Name == "Blessing");

        PromptScenario.RecordAsMock(expireResult.RawResponse, mockResponsesFolder: "TemporaryBuff", action: "SendAction");
    }

    // An effect on a MONSTER, not only on the hero — Storage.CollectEntityEffects has always collected
    // map.entities[i].effects the same way, but this was never checked against the real AI (all the other tests
    // in this file cover hero.effects only). We also check batching: if effects tick on both the hero AND a monster
    // in ONE round, Storage.TakeRoundBundle() collects them into ONE RoundBundle, and
    // AiContextBuilder.RoundTransitionState builds ONE message for both — so the real AI also
    // needs only ONE call, not one per effect owner (this matters for the number/cost of
    // requests — it must not grow linearly with the number of active periodic effects).
    [Fact]
    public async Task SimultaneousEffects_OnHeroAndMonster_AreCollectedAndResolvedInOneAiCall()
    {
        var storage = PromptScenario.LoadUiTestFixture("full_game.json");
        var cultist = storage.WorldState.Map.Entities!.Single(e => e.Name == "Cultist Morag"); // id 3, hp "23/33"
        int heroHpBefore = ParseHp(storage.WorldState.Hero!.Hp);
        int cultistHpBefore = ParseHp(cultist.Hp);

        storage.ApplyUpdateWorldState("""
            {"hero":{"effects":[{"name":"Burning","description":"Flames lick at your skin.","color":[220,120,40],
              "onRound":{"effect":"1 fire damage to hero"}}]},
             "map":{"entities":[{"id":3,"effects":[{"name":"Poisoned","description":"Poison in the blood.","color":[80,180,80],
              "onRound":{"effect":"1 poison damage to this entity"}}]}]}}
            """);

        // The very first TakeRoundBundle call on a fresh Storage — the dedup set is empty, both onRound effects with no
        // expiresAtRound tick right away (see Storage.CollectEntityEffects) — we deliberately don't
        // fast-forward the round, it isn't needed to check batching.
        var bundle = storage.TakeRoundBundle();
        Assert.NotNull(bundle);
        Assert.Equal(2, bundle.OnRoundEffects.Count);
        Assert.Contains(bundle.OnRoundEffects, e => e.OwnerPath == "hero" && e.Effect.Name == "Burning");
        Assert.Contains(bundle.OnRoundEffects, e => e.OwnerPath == "map.entities" && e.Effect.Name == "Poisoned");

        var ctx = new AiContextBuilder(storage.WorldState);
        string roundMessage = ctx.RoundTransitionState(bundle);
        Assert.Contains("Burning", roundMessage);
        Assert.Contains("Poisoned", roundMessage);
        _output.WriteLine("Combined round-transition request (both effects in one message):\n" + roundMessage);

        // ONE AI call — both effects must be resolved in this same response.
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, roundMessage);
        _output.WriteLine("Model response (both effects in one call):\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The tick of both effects must be described in history.");

        int heroHpAfter = ParseHp(storage.WorldState.Hero!.Hp);
        int cultistHpAfter = ParseHp(storage.WorldState.Map.Entities!.Single(e => e.Name == "Cultist Morag" && e.Deleted != true).Hp);

        Assert.True(heroHpAfter < heroHpBefore, $"The hero's HP must drop from \"Burning\": was {heroHpBefore}, now {heroHpAfter}.");
        Assert.True(cultistHpAfter < cultistHpBefore, $"The cultist's HP must drop from \"Poisoned\": was {cultistHpBefore}, now {cultistHpAfter}.");

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "MultiEffectRound", action: "SendAction");
    }

    // Grabbed (Grappled/Restrained) — per the prompt (§ hero.speedMax) speed drops to zero for the whole
    // duration of the effect via BOTH fields (speedMax AND speedLeft), not just a one-off speedLeft — the same
    // mistake that was fixed for Prone (see the Prone test above) must not repeat here.
    // Breaking free usually requires a roll (Athletics/Acrobatics) — per the prompt ANY check goes
    // through roll_dice, so rollValue:20 (auto natural 20) guarantees success and the test doesn't depend on
    // the model's luck.
    [Fact]
    public async Task Grabbed_SetsSpeedToZero_AndRestoresSpeedWhenHeroBreaksFree()
    {
        var storage = PromptScenario.LoadUiTestFixture("full_game.json");
        int originalSpeedMax = storage.WorldState.Hero!.SpeedMax ?? 30;
        Assert.True(storage.WorldState.Hero!.Effects is not { Count: > 0 },
            "Scenario precondition: the hero must start with no active effects.");

        var ctx = new AiContextBuilder(storage.WorldState);

        // The threat comes from the environment itself (the DM is free to introduce such a hazard) — it doesn't need
        // an entity already on the map or an ability of the hero, unlike the failed attempts
        // in the TemporaryBuff test above. The grab is described as something that HAS ALREADY HAPPENED (not "tries
        // to grab") — the first wording ("suddenly bursts out and coils around") left the AI
        // room for a saving throw, and it took it (a successful Strength roll WITHOUT calling
        // roll_dice, contrary to the prompt), avoiding the grab entirely — the effect wasn't added and the test failed not
        // because of a bug but because of the ambiguous scenario wording.
        string grabMessage = ctx.MinimalState(
            "a slimy tentacle from a crack in the wall has already wrapped tightly around my leg and holds me fast — I couldn't tear free right away, I'm stuck in place");
        var grabResult = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, grabMessage);
        _output.WriteLine("Model response (tentacle grab):\n" + grabResult.RawResponse);

        var grabParsed = PromptScenario.ApplyAndParse(storage, grabResult.RawResponse);
        Assert.True(grabParsed.HistoryTexts.Count > 0, "The tentacle grab must be described in history.");

        var activeEffects = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).ToList() ?? [];
        Assert.True(activeEffects.Count > 0,
            "After the grab the hero must get an active effect in hero.effects (not just text in history).");
        var grabbed = activeEffects[0];
        _output.WriteLine($"Added effect: \"{grabbed.Name}\" speedMax={storage.WorldState.Hero.SpeedMax} speedLeft={storage.WorldState.Hero.SpeedLeft}");
        Assert.False(string.IsNullOrWhiteSpace(grabbed.Name), "The added effect must have a name.");
        Assert.True(grabbed.Color is { Count: 3 }, "The added effect must have color:[R,G,B] (required by the prompt).");

        Assert.Equal(0, storage.WorldState.Hero.SpeedMax);
        Assert.Equal(0, storage.WorldState.Hero.SpeedLeft);

        string breakFreeMessage = ctx.MinimalState("I try to break free of the tentacle's grip, straining with all my might");
        var breakResult = await PromptScenario.CallRealAiWithAutoAnswersAsync(
            "Dnd5e", "SendAction", storage, breakFreeMessage, rollValue: 20);
        _output.WriteLine("Model response (breaking free):\n" + breakResult.RawResponse);
        Assert.True(breakResult.RollDiceTriggered, "Trying to break free is a check (Athletics/Acrobatics) and must go through roll_dice.");

        var breakParsed = PromptScenario.ApplyAndParse(storage, breakResult.RawResponse);
        Assert.True(breakParsed.HistoryTexts.Count > 0, "The attempt to break free must be described in history.");

        var stillActive = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).ToList() ?? [];
        Assert.DoesNotContain(stillActive, e => e.Name == grabbed.Name);
        Assert.Equal(originalSpeedMax, storage.WorldState.Hero.SpeedMax);

        PromptScenario.RecordAsMock(grabResult.RawResponse, mockResponsesFolder: "GrabbedEffect", action: "SendAction");
    }

    // Long rest — HP is restored, untilLongRest effects are removed, the time of day advances
    // 2 steps around the cycle Night→Morning→Day→Evening→Night (see § Time in the system prompt). The engine doesn't
    // track rest — it's entirely the AI's responsibility through a regular SendAction, with no separate "rest"
    // game action. The action is worded WITHOUT a specific target time ("until morning" and
    // the like) so as not to create a conflict between the player's narrative line and the mechanical rule
    // "+2 steps" — we're testing the rule itself, not whether the AI can count the hours until dawn.
    //
    // The fixture is burning_effect_game.json (empty map.entities), not full_game.json: on the first attempt
    // with full_game.json the AI CORRECTLY refused the rest — a hostile cultist and goblins are nearby,
    // and there can be no safe camp in the middle of a dungeon crawling with enemies (the same
    // class of "refusal under § ABSOLUTE PROHIBITIONS" already caught twice above). Here we check the
    // rest mechanics specifically, not whether the AI will let you sleep in the middle of a fight.
    [Fact]
    public async Task LongRest_RestoresHp_RemovesUntilLongRestEffects_AndAdvancesTimeOfDayByTwoSteps()
    {
        var storage = PromptScenario.LoadUiTestFixture("burning_effect_game.json");
        // id:0 is the fixture's preset "Burning" (not needed for this scenario — the AI would reasonably
        // not let a burning hero fall asleep peacefully) — we remove it with the same patch that adds "Exhaustion".
        storage.ApplyUpdateWorldState("""
            {"hero":{"hp":"3/10","effects":[{"id":0,"deleted":true},
              {"name":"Exhaustion","description":"You're worn out from the long march.","color":[160,160,160],"untilLongRest":true}]},
             "time":{"partOfDay":"Evening"}}
            """);

        var effectBefore = storage.WorldState.Hero!.Effects!.Single(e => e.Deleted != true);
        Assert.Equal("Exhaustion", effectBefore.Name);
        Assert.True(effectBefore.UntilLongRest, "Precondition: the effect must be marked untilLongRest:true.");

        var ctx = new AiContextBuilder(storage.WorldState);
        string restMessage = ctx.MinimalState("I stop for a long rest, make camp and rest");
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, restMessage);
        _output.WriteLine("Model response (long rest):\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The long rest must be described in history.");

        int hpAfter = ParseHp(storage.WorldState.Hero!.Hp);
        Assert.True(hpAfter > 4, $"HP must be restored after a long rest: was 4, now {hpAfter}.");

        var stillActive = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).ToList() ?? [];
        Assert.DoesNotContain(stillActive, e => e.Name == "Exhaustion");

        _output.WriteLine($"partOfDay after the rest: {storage.WorldState.Time.PartOfDay} (was \"Evening\", expecting +2 steps = \"Morning\")");
        Assert.Equal("Morning", storage.WorldState.Time.PartOfDay);

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "LongRest", action: "SendAction");
    }

    private static int ParseHp(string hp) => int.Parse(hp.Split('/')[0]);
}

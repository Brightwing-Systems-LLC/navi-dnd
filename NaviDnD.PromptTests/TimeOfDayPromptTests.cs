using NaviDnD.Clients;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// REAL Claude calls — see the SendActionPromptTests class for the general rules (costly, slow,
// run rarely and deliberately). Checks § Time/Rest in systemPrompt.md: the AI itself resets
// time.totalRounds to 1 and advances time.day when partOfDay changes (the engine doesn't track this —
// see Storage.ApplyTimeUpdate, it's a plain assignment with no automation at all), and also
// refuses a rest while periodic damage is active.
public class TimeOfDayPromptTests
{
    private readonly ITestOutputHelper _output;

    public TimeOfDayPromptTests(ITestOutputHelper output) => _output = output;

    // A long rest through midnight (Evening + 2 steps = Night → Morning) — the cycle passes through "Night", so
    // day must go up by 1 and totalRounds must reset to 1 no matter how large the value was
    // before the rest (519 is a realistic "stale" value, as in the production-log analysis that
    // gave rise to this rule).
    [Fact]
    public async Task LongRest_ResetsTotalRounds_AndIncrementsDay_WhenCycleCrossesNight()
    {
        var storage = PromptScenario.LoadUiTestFixture("burning_effect_game.json");
        // id:0 is the fixture's preset "Burning", not needed for this scenario (the AI would reasonably not
        // let a burning hero fall asleep peacefully, see the similar note in EffectsPromptTests).
        storage.ApplyUpdateWorldState("""
            {"hero":{"hp":"4/10","effects":[{"id":0,"deleted":true}]},
             "time":{"totalRounds":519,"partOfDay":"Evening","day":3}}
            """);

        var ctx = new AiContextBuilder(storage.WorldState);
        string restMessage = ctx.MinimalState("I stop for a long rest, make camp and rest until morning");
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, restMessage);
        _output.WriteLine("Model response (long rest through midnight):\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The long rest must be described in history.");

        _output.WriteLine($"After the rest: totalRounds={storage.WorldState.Time.TotalRounds} day={storage.WorldState.Time.Day} partOfDay={storage.WorldState.Time.PartOfDay}");
        Assert.Equal("Morning", storage.WorldState.Time.PartOfDay);
        Assert.Equal(1, storage.WorldState.Time.TotalRounds);
        Assert.Equal(4, storage.WorldState.Time.Day);

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "LongRestRoundReset", action: "SendAction");
    }

    // A short rest (Morning + 1 step = Day, "Night" isn't crossed — day doesn't change). It also
    // checks the asymmetry between finite and untilLongRest effects on a short rest (see § Rest): "Bruised"
    // with expiresAtRound must be removed (the rest's duration covers 2 rounds), while "Exhaustion"
    // (untilLongRest) goes only on a long rest — the short one doesn't touch it.
    [Fact]
    public async Task ShortRest_AdvancesOneStep_ResetsRound_KeepsUntilLongRestEffect_RemovesFiniteEffect()
    {
        var storage = PromptScenario.LoadUiTestFixture("burning_effect_game.json");
        storage.ApplyUpdateWorldState("""
            {"hero":{"hp":"8/10","effects":[{"id":0,"deleted":true},
              {"name":"Bruised","description":"Still aching from the blow.","color":[160,160,160],"expiresAtRound":3},
              {"name":"Exhaustion","description":"You're worn out from the long march.","color":[160,160,160],"untilLongRest":true}]},
             "time":{"totalRounds":45,"partOfDay":"Morning","day":1}}
            """);

        var ctx = new AiContextBuilder(storage.WorldState);
        string restMessage = ctx.MinimalState("I take a short rest for an hour, resting against the wall");
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, restMessage);
        _output.WriteLine("Model response (short rest):\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The short rest must be described in history.");

        _output.WriteLine($"After the rest: totalRounds={storage.WorldState.Time.TotalRounds} day={storage.WorldState.Time.Day} partOfDay={storage.WorldState.Time.PartOfDay}");
        Assert.Equal("Day", storage.WorldState.Time.PartOfDay);
        Assert.Equal(1, storage.WorldState.Time.TotalRounds);
        Assert.Equal(1, storage.WorldState.Time.Day);

        var stillActive = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).Select(e => e.Name).ToList() ?? [];
        Assert.DoesNotContain("Bruised", stillActive);
        Assert.Contains("Exhaustion", stillActive);

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "ShortRest", action: "SendAction");
    }

    // Active periodic damage ("Poisoned", no expiresAtRound — indefinite, the worst case) — by an
    // explicit design decision the rest must be REFUSED outright, not allowed with damage "made up"
    // for the skipped hours: partOfDay/totalRounds must not move, and the effect itself must stay.
    [Fact]
    public async Task Rest_BlockedByActiveDamagingPeriodicEffect_RefusesRest()
    {
        var storage = PromptScenario.LoadUiTestFixture("burning_effect_game.json");
        storage.ApplyUpdateWorldState("""
            {"hero":{"hp":"8/10","effects":[{"id":0,"deleted":true},
              {"name":"Poisoned","description":"Poison in your blood burns your veins.","color":[80,180,80],
               "onRound":{"effect":"1 poison damage to hero"}}]},
             "time":{"totalRounds":50,"partOfDay":"Evening","day":2}}
            """);

        var ctx = new AiContextBuilder(storage.WorldState);
        string restMessage = ctx.MinimalState("I stop for a long rest, make camp and rest until morning");
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, restMessage);
        _output.WriteLine("Model response (rest while poisoned — expecting a refusal):\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The refusal to rest must be explained in history.");

        _output.WriteLine($"After the attempt: totalRounds={storage.WorldState.Time.TotalRounds} partOfDay={storage.WorldState.Time.PartOfDay}");
        Assert.Equal("Evening", storage.WorldState.Time.PartOfDay);
        Assert.Equal(50, storage.WorldState.Time.TotalRounds);

        var stillActive = storage.WorldState.Hero!.Effects?.Where(e => e.Deleted != true).Select(e => e.Name).ToList() ?? [];
        Assert.Contains("Poisoned", stillActive);

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "RestBlockedByPoison", action: "SendAction");
    }
}

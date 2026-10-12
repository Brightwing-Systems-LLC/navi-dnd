using NaviDnD.Clients;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// REAL Claude calls — see the SendActionPromptTests class for the general rules (costly, slow,
// run rarely and deliberately). The combat scenarios check § COMBAT from systemPrompt.md, in particular
// item 10 of the prompt's self-check: the final entry of the enemies' turn MUST hand the turn back to the hero,
// advance the round and reset actions/speed — if the prompt stops doing this, the test fails.
public class CombatPromptTests
{
    private readonly ITestOutputHelper _output;

    public CombatPromptTests(ITestOutputHelper output) => _output = output;

    // The same scenario as NaviDnD.UiTests.OpportunityAttackScenarioTests — the hero moves away from
    // a cell adjacent to the orc during combat → the opportunity_attack_on_hero trigger (the creature attacks
    // the hero on its own, without ask_player — "this is not the hero's choice", see the prompt).
    [Fact]
    public async Task OpportunityAttackOnHero_ResolvesWithoutAskingPlayer()
    {
        var storage = PromptScenario.LoadUiTestFixture("opportunity_attack_game.json");
        var orc = storage.WorldState.Map.Entities![0]; // "Orc"

        var ctx = new AiContextBuilder(storage.WorldState);
        string userMessage = ctx.TriggerState([(orc, "opportunity_attack_on_hero")], [], []);

        // The auto-responder is a safety net: the prompt explicitly forbids ask_player for this trigger, but if
        // the model calls it anyway, the test must not hang until the real MCP timeout (50s).
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, userMessage);
        _output.WriteLine("Model response:\n" + result.RawResponse);

        Assert.False(result.AskPlayerTriggered, "The prompt explicitly forbids ask_player for opportunity_attack_on_hero — it's not the hero's choice.");

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The opportunity attack must be described in history.");
        Assert.Contains(parsed.HistoryTexts, t => t.Contains('[') && t.Contains(']'));

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "OpportunityAttack", action: "SendAction");
    }

    // The same scenario as NaviDnD.UiTests.EnemyTurnBehaviorScenarioTests — a goblin's turn
    // (CombatTurnState). Checks item 10 of the prompt's self-check: the final entry MUST hand the
    // turn back to the hero, advance the round and reset the hero's actions/speed to maximum.
    [Fact]
    public async Task EnemyTurn_FinalEntryHandsControlBackToHero()
    {
        var storage = PromptScenario.LoadUiTestFixture("enemy_turn_game.json");
        int initialRound = storage.WorldState.Time.TotalRounds;
        string heroSymbol = storage.WorldState.Hero!.Symbol!;

        var ctx = new AiContextBuilder(storage.WorldState);
        string userMessage = ctx.CombatTurnState();

        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, userMessage);
        _output.WriteLine("Model response:\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The enemy's turn must be described in history.");

        Assert.Equal(heroSymbol, storage.WorldState.Combat?.CurrentTurn);
        Assert.True(storage.WorldState.Time.TotalRounds > initialRound,
            $"The round should have advanced: was {initialRound}, now {storage.WorldState.Time.TotalRounds}.");

        var actions = storage.WorldState.Hero!.Actions ?? [];
        Assert.True(actions.Count > 0, "The hero must still have actions (Action/Bonus action/Reaction).");
        Assert.All(actions.Where(a => a.Deleted != true), a =>
            Assert.True(a.Value == a.MaxValue, $"Action \"{a.Name}\" must be reset to maximum: value={a.Value}, maxValue={a.MaxValue}."));
        Assert.Equal(storage.WorldState.Hero.SpeedMax, storage.WorldState.Hero.SpeedLeft);

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "EnemyAttack", action: "SendAction");
    }
}

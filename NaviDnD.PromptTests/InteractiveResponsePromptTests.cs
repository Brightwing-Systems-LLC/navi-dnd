using NaviDnD.Clients;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// REAL Claude calls — see the SendActionPromptTests class for the general rules (costly, slow,
// run rarely and deliberately). Checks that the prompt actually CALLS the required MCP tool
// (roll_dice / ask_player) in scenarios that need it — without the auto-responder
// (PromptScenario.CallRealAiWithAutoAnswersAsync) the call would hang until the real tool timeout (50s).
public class InteractiveResponsePromptTests
{
    private readonly ITestOutputHelper _output;

    public InteractiveResponsePromptTests(ITestOutputHelper output) => _output = output;

    // A sword attack on a visible enemy in combat — must call roll_dice (see the prompt: "roll_dice —
    // all of the hero's d20 rolls (... attack ...)"). The scenario is the same as OpportunityAttack (hero and
    // orc adjacent, hero's turn, full resources).
    [Fact]
    public async Task AttackAction_TriggersRollDice()
    {
        var storage = PromptScenario.LoadUiTestFixture("opportunity_attack_game.json");
        var actionResource = storage.WorldState.Hero!.Actions!.First(a => a.Name == "Action");
        Assert.Equal(1, actionResource.Value); // precondition: the Action hasn't been spent yet

        var ctx = new AiContextBuilder(storage.WorldState);
        string userMessage = ctx.MinimalState("I attack the orc with my sword");

        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync(
            "Dnd5e", "SendAction", storage, userMessage, rollValue: 15);
        _output.WriteLine("Model response:\n" + result.RawResponse);

        Assert.True(result.RollDiceTriggered, "An attack on a visible enemy should have called roll_dice.");

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The result of the attack must be described in history.");
        Assert.Contains(parsed.HistoryTexts, t => t.Contains('[') && t.Contains(']'));

        var actionAfter = storage.WorldState.Hero!.Actions!.First(a => a.Name == "Action");
        Assert.Equal(0, actionAfter.Value); // the "Action" was spent on the attack

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "DiceRoll", action: "SendAction");
    }

    // Two identical enemies next to the hero + the ambiguous command "I attack the enemy" — the prompt must
    // let the player pick the target with the mouse on the map (select_target, see § MCP), not ask in text.
    [Fact]
    public async Task AmbiguousTarget_TriggersSelectTarget()
    {
        var storage = PromptScenario.LoadLocalFixture("ambiguous_target_game.json");

        var ctx = new AiContextBuilder(storage.WorldState);
        string userMessage = ctx.MinimalState("I attack the enemy with my sword");

        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync(
            "Dnd5e", "SendAction", storage, userMessage, rollValue: 15);
        _output.WriteLine("Model response:\n" + result.RawResponse);

        Assert.True(result.SelectTargetTriggered,
            "An ambiguous target (two identical enemies nearby) should have called select_target.");

        // The response after the target is clarified must be structurally valid (we don't check the specific target —
        // the model decides on its own and may have added extra context).
        PromptScenario.ApplyAndParse(storage, result.RawResponse);

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "AskPlayer", action: "SendAction");
    }
}

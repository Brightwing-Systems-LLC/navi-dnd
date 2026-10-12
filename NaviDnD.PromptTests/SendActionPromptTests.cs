using NaviDnD.Clients;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// REAL Claude calls — costly and slow (seconds per test), so the project is NOT part of the solution-wide
// test run. Run deliberately and RARELY — only when you change a specific systemPrompt.md and
// want to check/update the mock for the change, not as part of everyday work:
//   dotnet test NaviDnD.PromptTests
// To update the canonical mock for NaviDnD.UiTests from the same run:
//   NAVIDND_RECORD_PROMPTS=1 dotnet test NaviDnD.PromptTests --filter OnStepTrap
public class SendActionPromptTests
{
    private readonly ITestOutputHelper _output;

    public SendActionPromptTests(ITestOutputHelper output) => _output = output;

    // The same scenario as NaviDnD.UiTests.TriggersScenarioTests.OnStepTrap_MovingOntoIt_FiresAiTrigger —
    // the hero steps on the "Bear Trap" (triggers.onStep, once:true).
    [Fact]
    public async Task OnStepTrap_DealsDamageAndRemovesTrap()
    {
        var storage = PromptScenario.LoadUiTestFixture("trap_game.json");
        var trap = storage.WorldState.Map.Objects![0]; // "Bear Trap" — the only object in the fixture
        int initialHp = ParseHp(storage.WorldState.Hero!.Hp);

        var ctx = new AiContextBuilder(storage.WorldState);
        string userMessage = ctx.TriggerState([(trap, "onStep")], [], []);

        string response = await PromptScenario.CallRealAiAsync("Dnd5e", "SendAction", storage, userMessage);
        _output.WriteLine("Model response:\n" + response);

        var result = PromptScenario.ApplyAndParse(storage, response);

        Assert.True(result.HistoryTexts.Count > 0, "The response contains no history entries.");
        Assert.Contains(result.HistoryTexts, t => t.Contains('[') && t.Contains(']'));

        int finalHp = ParseHp(storage.WorldState.Hero!.Hp);
        Assert.True(finalHp < initialHp, $"The hero's HP should have dropped from the trap: was {initialHp}, now {finalHp}.");

        Assert.DoesNotContain(storage.WorldState.Map.Objects ?? [], o => o.Name == "Bear Trap" && o.Deleted != true);

        PromptScenario.RecordAsMock(response, mockResponsesFolder: "Triggers", action: "SendAction");
    }

    // The same scenario as NaviDnD.UiTests.TriggersScenarioTests.HiddenObjectOnVisible... — the hero
    // comes within sight range of a hidden object (triggers.onVisible, once:true).
    [Fact]
    public async Task HiddenObjectOnVisible_RevealsTheObject()
    {
        var storage = PromptScenario.LoadUiTestFixture("hidden_object_game.json");
        var hiddenObject = storage.WorldState.Map.Objects![0]; // "Secret Sigil"

        var ctx = new AiContextBuilder(storage.WorldState);
        string userMessage = ctx.TriggerState([(hiddenObject, "onVisible")], [], []);

        string response = await PromptScenario.CallRealAiAsync("Dnd5e", "SendAction", storage, userMessage);
        _output.WriteLine("Model response:\n" + response);

        var result = PromptScenario.ApplyAndParse(storage, response);

        Assert.True(result.HistoryTexts.Count > 0, "The response contains no history entries — discovering the hidden object must be described.");

        // The object is either marked visible (hidden:false) or removed from the list entirely — both options
        // are valid per the prompt ("hidden: to clear — {id,hidden:false}"), as long as the object no longer
        // stays Hidden:true (otherwise, from the player's point of view, the trigger effectively didn't fire).
        var stillHidden = storage.WorldState.Map.Objects?.Any(o => o.Name == "Secret Sigil" && o.Hidden == true) ?? false;
        Assert.False(stillHidden, "The hidden object should have been revealed (hidden:false) or removed — but it stayed Hidden:true.");

        PromptScenario.RecordAsMock(response, mockResponsesFolder: "HiddenTrigger", action: "SendAction");
    }

    private static int ParseHp(string hp) => int.Parse(hp.Split('/')[0]);
}

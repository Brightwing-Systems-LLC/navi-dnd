using NaviDnD.Clients;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// REAL Claude calls — see the SendActionPromptTests class for the general rules (costly, slow,
// run rarely and deliberately).
public class InventoryPromptTests
{
    private readonly ITestOutputHelper _output;

    public InventoryPromptTests(ITestOutputHelper output) => _output = output;

    // The same scenario as NaviDnD.UiTests.InventoryScenarioTests — the hero puts away the bow and takes the sword
    // in hand. A regular (non-trigger) action — MinimalState, just as the game actually sends it.
    [Fact]
    public async Task EquipSword_UnequipsBow_AndEquipsSwordInHand()
    {
        var storage = PromptScenario.LoadUiTestFixture("full_game.json");
        var bow = storage.WorldState.Hero!.Inventory!.First(i => i.Name == "Longbow");
        var sword = storage.WorldState.Hero!.Inventory!.First(i => i.Name == "Longsword");
        Assert.True(bow.EquipmentedSlot is { Count: > 0 }, "Scenario precondition: the bow must start equipped.");
        Assert.True(sword.EquipmentedSlot is not { Count: > 0 }, "Scenario precondition: the sword must start NOT equipped.");

        var ctx = new AiContextBuilder(storage.WorldState);
        string userMessage = ctx.MinimalState("I sling the bow onto my back and take the longsword in hand");

        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, userMessage);
        _output.WriteLine("Model response:\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The change of equipment must be described in history.");

        var bowAfter = storage.WorldState.Hero!.Inventory!.First(i => i.Name == "Longbow");
        var swordAfter = storage.WorldState.Hero!.Inventory!.First(i => i.Name == "Longsword");

        Assert.True(bowAfter.EquipmentedSlot is not { Count: > 0 }, "The bow must be unequipped (equipmentedSlot empty).");
        Assert.True(swordAfter.EquipmentedSlot is { Count: > 0 }, "The sword must be equipped in hand (equipmentedSlot not empty).");

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "Inventory", action: "SendAction");
    }
}

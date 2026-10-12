using NaviDnD.Clients;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// REAL Claude calls — see the SendActionPromptTests class for the general rules (costly, slow,
// run rarely and deliberately). Checks the fix for a real bug from a production log: the AI narrated "I walk up
// and open the door" even though the hero hadn't moved and was two cells away from the door — the engine didn't
// check this at all, the prompt relied on the AI not inventing an approach that never happened.
// Now § MAP AND OBJECTS teaches it to call open_door(id)/close_door(id) — the tool measures the distance itself
// (Chebyshev ≤1 to from/to) and returns either a patch or the reason for refusing.
public class DoorInteractionPromptTests
{
    private readonly ITestOutputHelper _output;

    public DoorInteractionPromptTests(ITestOutputHelper output) => _output = output;

    // Hero at [1,1], door between [3,3]/[4,3] — far away (Chebyshev 2/3). open_door must refuse,
    // and the AI must not resolve the opening on its own on top of the tool's refusal.
    [Fact]
    public async Task OpenDoor_WhenHeroIsFar_DoesNotFabricateSuccess()
    {
        var storage = PromptScenario.LoadLocalFixture("door_interaction_game.json");
        var doorBefore = storage.WorldState.Map.Doors!.Single();
        Assert.False(doorBefore.IsDoorOpen, "Precondition: the door starts closed.");

        var ctx = new AiContextBuilder(storage.WorldState);
        string message = ctx.MinimalState("I walk up and open the door");
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, message);
        _output.WriteLine("Model response (door far away):\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "The refusal/clarification must be described in history.");

        var doorAfter = storage.WorldState.Map.Doors!.Single();
        Assert.NotEqual(true, doorAfter.IsDoorOpen);

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "DoorTooFar", action: "SendAction");
    }

    // The hero stands right next to the door (id:0, from:[3,3]) — open_door must return a patch, and the AI must
    // apply it.
    [Fact]
    public async Task OpenDoor_WhenHeroIsAdjacent_OpensIt()
    {
        var storage = PromptScenario.LoadLocalFixture("door_interaction_game.json");
        storage.ApplyUpdateWorldState("""{"hero":{"position":[3,3]}}""");

        var ctx = new AiContextBuilder(storage.WorldState);
        string message = ctx.MinimalState("I open the door in front of me");
        var result = await PromptScenario.CallRealAiWithAutoAnswersAsync("Dnd5e", "SendAction", storage, message);
        _output.WriteLine("Model response (door adjacent):\n" + result.RawResponse);

        var parsed = PromptScenario.ApplyAndParse(storage, result.RawResponse);
        Assert.True(parsed.HistoryTexts.Count > 0, "Opening the door must be described in history.");

        var doorAfter = storage.WorldState.Map.Doors!.Single();
        Assert.True(doorAfter.IsDoorOpen, "The door next to the hero must be open.");

        PromptScenario.RecordAsMock(result.RawResponse, mockResponsesFolder: "DoorAdjacent", action: "SendAction");
    }
}

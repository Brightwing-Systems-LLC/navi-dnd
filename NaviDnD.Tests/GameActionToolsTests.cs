using System.Text.Json;
using NaviDnD.McpServer;

namespace NaviDnD.Tests;

// Unit tests for the door_action MCP tool without calling the model — they check the adjacency
// geometry itself. Real bug: the player stood "one cell away" from a door (Chebyshev distance 1 to
// the nearest end From/To) and door_action opened it anyway — the code allowed dist<=1, although a door
// is the boundary BETWEEN From and To, not a separate cell: the hero must stand exactly on one of them.
public class GameActionToolsTests : IDisposable
{
    private readonly List<string> _files = [];

    // State files and everything the tool puts next to them (navidnd_test_<guid>*) are removed after each test.
    public void Dispose()
    {
        foreach (string path in _files)
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "*"))
                try { File.Delete(file); } catch (IOException) { }
    }

    private GameActionTools CreateTools(string heroPositionJson, string doorsJson)
    {
        string json = """
            {"hero":{"symbol":"HRO","name":"Tester","hp":"10/10","position":HERO_POSITION},
             "map":{"cols":20,"rows":15,"doors":DOORS}}
            """
            .Replace("HERO_POSITION", heroPositionJson)
            .Replace("DOORS", doorsJson);
        string tempPath = Path.Combine(Path.GetTempPath(), $"navidnd_test_{Guid.NewGuid():N}.json");
        _files.Add(tempPath);
        File.WriteAllText(tempPath, json);
        return new GameActionTools(new McpServerConfig(tempPath, LogPath: null));
    }

    private const string OneDoor = """[{"from":[5,5],"to":[6,5],"isDoor":true,"color":[139,90,43],"isDoorOpen":false}]""";

    [Fact]
    public void DoorAction_HeroStandingOnFrom_Succeeds()
    {
        var tools = CreateTools("[5,5]", OneDoor);
        var result = Parse(tools.DoorAction(0, "open"));
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void DoorAction_HeroStandingOnTo_Succeeds()
    {
        var tools = CreateTools("[6,5]", OneDoor);
        var result = Parse(tools.DoorAction(0, "close"));
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
    }

    // Real bug: the hero at [7,5] is one cell BEYOND the door's end To:[6,5] (Chebyshev distance
    // to the nearest end = 1). A whole cell [6,5] lies between the hero and the door; he hasn't reached it.
    [Fact]
    public void DoorAction_HeroOneCellPastDoor_Fails()
    {
        var tools = CreateTools("[7,5]", OneDoor);
        var result = Parse(tools.DoorAction(0, "open"));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("too far", result.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void DoorAction_HeroFarAway_Fails()
    {
        var tools = CreateTools("[15,10]", OneDoor);
        var result = Parse(tools.DoorAction(0, "open"));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
    }

    // hide/reveal isn't a physical interaction (the DM marks a secret / reveals what was found),
    // so adjacency isn't required.
    [Fact]
    public void DoorAction_HideAndReveal_DoNotRequireAdjacency()
    {
        var tools = CreateTools("[15,10]", OneDoor);

        var hideResult = Parse(tools.DoorAction(0, "hide"));
        Assert.True(hideResult.RootElement.GetProperty("success").GetBoolean());

        var revealResult = Parse(tools.DoorAction(0, "reveal"));
        Assert.True(revealResult.RootElement.GetProperty("success").GetBoolean());
    }

    // break is a physical action (the hero hacks/kicks the door down) and requires the same adjacency as
    // open/close.
    [Fact]
    public void DoorAction_Break_RequiresAdjacency()
    {
        var farTools = CreateTools("[15,10]", OneDoor);
        Assert.False(Parse(farTools.DoorAction(0, "break")).RootElement.GetProperty("success").GetBoolean());

        var nearTools = CreateTools("[5,5]", OneDoor);
        Assert.True(Parse(nearTools.DoorAction(0, "break")).RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void DoorAction_Passage_NotAPhysicalDoor_Refuses()
    {
        const string passage = """[{"from":[5,5],"to":[6,5],"isDoor":false}]""";
        var tools = CreateTools("[5,5]", passage);
        var result = Parse(tools.DoorAction(0, "open"));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void DoorAction_UnknownDoorId_ReturnsError()
    {
        var tools = CreateTools("[5,5]", OneDoor);
        var result = Parse(tools.DoorAction(5, "open"));
        Assert.True(result.RootElement.TryGetProperty("error", out _));
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);
}

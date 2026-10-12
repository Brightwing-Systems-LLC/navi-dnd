namespace NaviDnD.Clients;

// Shared by all CLI providers (Claude, Codex): which MCP tools each action needs and how to launch
// the game's MCP server (NaviDnD.McpServer) for a request.
internal static class AiActionTools
{
    // MCP server tools allowed for an action (the action name is the prompt folder: SendAction, StartNewGame…).
    public static string[] ToolsFor(string action) => action switch
    {
        "CreateNewGame" => ["find_icon", "find_spell", "find_item"],
        // The AI sets the location plan (plan_location), the code builds the geometry — generate_map is not needed.
        "StartNewGame" => ["plan_location", "find_assets", "find_icon", "find_monster", "add_place"],
        // World concept and hero description — from the data in the message, no tools needed.
        "CreateWorld" or "DescribeHero" or "RepairJson" or "LevelUpApply" => [],
        // Level up: spell options come from the reference.
        "LevelUpOptions" => ["find_spell"],
        "PopulateChunk" => ["get_world_state", "find_assets", "find_icon", "find_monster", "find_item", "world_query"],
        _ => ["get_world_state", "calculate_movement", "roll_dice", "use_reaction", "select_target",
              "advance_round", "long_rest", "short_rest", "door_action", "find_icon",
              "find_spell", "find_item", "world_query", "add_place", "plan_location"],
    };

    // Actions before the world/hero is created — the MCP server reads the draft (newGameState.json), not the game save.
    public static readonly HashSet<string> DraftActions = ["CreateNewGame", "FixHeroForNewGame", "StartNewGame", "DescribeHero"];

    // Actions without a dialog window: ask_player/roll_dice would hang there until the timeout.
    public static readonly HashSet<string> NonInteractiveActions = ["CreateNewGame", "StartNewGame", "DescribeHero", "CreateWorld", "RepairJson", "LevelUpOptions", "LevelUpApply"];

    // MCP server launch arguments for an action; withToolFilter — the server itself exposes only ToolsFor(action)
    // (for clients without their own allowed-tools list).
    public static List<string> McpArgs(string action, AiLogger? logger, bool withToolFilter)
    {
        string worldStatePath = DraftActions.Contains(action) ? Storage.DraftSavePath : Storage.SavePath;
        var args = new List<string> { "--world-state", worldStatePath };
        if (logger != null) args.AddRange(["--log", logger.LogPath]);
        args.AddRange(["--run-id", Guid.NewGuid().ToString("N")]);
        if (NonInteractiveActions.Contains(action)) args.Add("--no-interactive");
        if (withToolFilter) args.AddRange(["--tools", string.Join(",", ToolsFor(action).DefaultIfEmpty("none"))]);
        return args;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NaviDnD.McpServer;

string worldStatePath = args
    .SkipWhile(a => a != "--world-state")
    .Skip(1)
    .FirstOrDefault()
    ?? throw new ArgumentException("Required argument missing: --world-state <path>");

string? logPath = args
    .SkipWhile(a => a != "--log")
    .Skip(1)
    .FirstOrDefault();

// The use_reaction/roll_dice tools wait for the player's answer through the dialog window (DialogDisplay).
// For actions without such a UI (CreateNewGame, StartNewGame) the client passes --no-interactive,
// otherwise they just hang until the timeout.
bool interactive = !args.Contains("--no-interactive");

// --tools a,b,c — expose only these tools to the client (Claude CLI does this with --allowedTools; other
// clients, e.g. Codex CLI, may have no such flag — then the filter is here, on the server side).
string? toolsArg = args.SkipWhile(a => a != "--tools").Skip(1).FirstOrDefault();
HashSet<string>? allowedTools = toolsArg == null ? null
    : [.. toolsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

if (!File.Exists(worldStatePath))
    throw new FileNotFoundException($"worldState.json not found: {worldStatePath}");

// The game's language (the save's language field): names from the catalogs and the generator are in it, as the DM writes.
try
{
    NaviDnD.L.SetWorld(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(worldStatePath))?["language"]?.GetValue<string>());
}
catch { /* unreadable — keep the default language */ }

var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

// All logs go to stderr — stdout must stay clean for JSON-RPC messages
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(new McpServerConfig(worldStatePath, logPath));
var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<WorldStateTools>()
    .WithTools<MapGenTools>()
    .WithTools<CalculateMovementTools>()
    .WithTools<GameActionTools>()
    .WithTools<IconTools>()
    .WithTools<SpellTools>()
    .WithTools<MonsterTools>()
    .WithTools<AssetSearchTools>()
    .WithTools<EquipmentTools>()
    .WithTools<WorldQueryTools>();

if (interactive)
{
    mcpBuilder
        .WithTools<UseReactionTools>()
        .WithTools<RollDiceTools>()
        .WithTools<SelectTargetTools>();
}

if (allowedTools != null)
    builder.Services.PostConfigure<McpServerOptions>(options =>
    {
        if (options.ToolCollection is not { } tools) return;
        foreach (var tool in tools.ToList())
            if (!allowedTools.Contains(tool.ProtocolTool.Name)) tools.Remove(tool);
    });

await builder.Build().RunAsync();

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NaviDnD.Helpers;

namespace NaviDnD.Clients;

// AI via the OpenAI Codex CLI (`codex exec`) — instead of the Claude CLI (the "AI" setting). Same prompt and same
// game MCP server: dice, questions to the player, target selection, plan_location etc. work the same way.
//  - the DM rules replace the coding-assistant instructions via model_instructions_file; the context goes to stdin;
//  - the MCP server — via config overrides `-c mcp_servers.navidnd.*` (the user's own config.toml is left
//    untouched); the server itself exposes the action's tool set (`--tools`, AiActionTools), since Codex has no
//    equivalent of --allowedTools;
//  - read-only sandbox: the Codex agent does not change files or run modifying commands;
//  - the response is the agent's last message (`-o file`), and the `--json` events are for errors and the log.
// Model — CodexModel from settings (empty — the Codex default model); Claude models from the request do not apply.
public class CodexCliAiProvider(AppConfig config, AiLogger? logger = null) : IAiProvider
{
    // Codex is a coding agent; here it is the game's DM: only a response in the prompt's format and the game's MCP tools.
    private const string Preface =
        "You are working inside a game as its Dungeon Master, not as a coding assistant: do not read or change files, do not run " +
        "shell commands. Of the tools, use only the navidnd MCP server. The response is strictly in the format from " +
        "the instructions below, with no explanation around it.\n\n";

    public Task<string> Complete(IReadOnlyList<string> systemBlocks, string userMessage, string actionPath, string model) =>
        WithRetry(() => CompleteOnce(systemBlocks, userMessage, actionPath), AiActionTools.ToolsFor(Path.GetFileName(actionPath)).Length == 0);

    // The game reads the response whole (SequentialHistoryPlayer), and the Codex stream only emits finished agent messages —
    // so the "stream" is the whole response in one chunk.
    public async Task<string> CompleteWithStreaming(IReadOnlyList<string> systemBlocks, string userMessage, string actionPath,
        string model, Action<string>? onChunk)
    {
        string response = await Complete(systemBlocks, userMessage, actionPath, model);
        onChunk?.Invoke(response);
        return response;
    }

    // Overload/network failure on the OpenAI side — retry only for requests without game tools.
    private static bool IsTransient(Exception ex) =>
        ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("stream disconnected", StringComparison.OrdinalIgnoreCase);

    private async Task<string> WithRetry(Func<Task<string>> call, bool canRetry)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await call(); }
            // MCP may have already changed the world or taken a roll: retrying the whole turn would repeat the side effect.
            catch (Exception ex) when (ex is not AiSetupException && canRetry && attempt < 3 && IsTransient(ex))
            {
                logger?.LogNote($"Codex: transient error (attempt {attempt}) — retrying in {attempt * 5} s");
                await Task.Delay(attempt * 5000);
            }
        }
    }

    private async Task<string> CompleteOnce(IReadOnlyList<string> systemBlocks, string userMessage, string actionPath)
    {
        string action = Path.GetFileName(actionPath);
        string workDir = Path.Combine(Path.GetTempPath(), $"navidnd-codex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string lastMessagePath = Path.Combine(workDir, "last-message.txt");
        string instructionsPath = Path.Combine(workDir, "instructions.md");
        try
        {
            string userPart = systemBlocks.Count > 1 ? string.Join("\n\n", systemBlocks.Skip(1)) + "\n\n" + userMessage : userMessage;
            if (systemBlocks.Count == 0) throw new ArgumentException("The DM system prompt is not set.", nameof(systemBlocks));
            await File.WriteAllTextAsync(instructionsPath, Preface + systemBlocks[0], new UTF8Encoding(false));
            logger?.LogRequest(action, systemBlocks, userPart);
            logger?.LogNote($"Codex: model={ModelFor(action)}, reasoning={ReasoningFor(action)}");

            var psi = BuildPsi(workDir, lastMessagePath, instructionsPath, action);
            var sw = Stopwatch.StartNew();
            Process? started;
            try { started = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
            {
                throw new AiSetupException(L.F("Codex CLI not found ({0}): install it (npm i -g @openai/codex), sign in (codex login) and set the path in the settings (\"CODEX PATH\") if needed.",
                    config.CodexCliPath));
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                throw new AiSetupException(L.T("Codex CLI can't be started.\nCheck permissions, antivirus blocking and \"CODEX PATH\" in the settings."));
            }
            using var process = started ?? throw new Exception(L.T("Could not start Codex CLI."));
            ChildProcessJob.Add(process);   // the game was closed — the request ends with it
            await process.StandardInput.WriteAsync(userPart);
            process.StandardInput.Close();

            var stderrTask = process.StandardError.ReadToEndAsync();
            var events = new StringBuilder();
            string? lastAgentText = null, error = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                events.AppendLine(line);
                var (text, err) = ParseEvent(line);
                if (text != null) lastAgentText = text;
                if (err != null) error = err;
            }
            await process.WaitForExitAsync();
            string stderr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(stderr)) logger?.LogNote($"STDERR: {stderr.Trim()}");

            string? final = File.Exists(lastMessagePath) ? File.ReadAllText(lastMessagePath, Encoding.UTF8) : null;
            if (string.IsNullOrWhiteSpace(final) && process.ExitCode == 0 && error == null) final = lastAgentText;
            if (process.ExitCode != 0 || error != null || string.IsNullOrWhiteSpace(final))
            {
                string reason = error ?? (process.ExitCode != 0 ? L.F("code {0}", process.ExitCode) : L.T("empty response"));
                if (!string.IsNullOrWhiteSpace(stderr)) reason += $"; stderr: {stderr.Trim()}";
                logger?.LogResponse(events.ToString(), $"ERROR: {reason}", sw.Elapsed);
                throw CreateError($"Codex CLI: {reason}");
            }
            string response = StripMarkdownFences(final);
            logger?.LogResponse(events.ToString(), response, sw.Elapsed);
            return response;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (Exception ex) { logger?.LogNote($"Could not delete the temp folder {workDir}: {ex.Message}"); }
        }
    }

    public static Exception CreateError(string rawMessage)
    {
        string[] authMarkers = ["not logged in", "not authenticated", "unauthorized", "authentication_error",
            "authentication required", "authentication token", "failed to authenticate", "please log in",
            "please login", "codex login", "token expired", "token has expired", "refresh_token_reused",
            "invalid_api_key", "incorrect api key", "401"];
        if (authMarkers.Any(marker => rawMessage.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return new AiSetupException(L.T("Codex is not signed in or the session expired.\nRun codex login in a terminal and try again."));
        string[] accessMarkers = ["403", "model_not_found", "does not have access", "do not have access",
            "insufficient_quota", "usage limit", "usage_limit"];
        if (accessMarkers.Any(marker => rawMessage.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return new AiSetupException(L.T("Codex: no access to the model or the limit is reached.\nCheck your account, subscription, limits and the model in the settings."));
        return new Exception(rawMessage);
    }

    private ProcessStartInfo BuildPsi(string workDir, string lastMessagePath, string instructionsPath, string action)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveCodex(config.CodexCliPath),
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in new[] { "exec", "--json", "--ephemeral", "--ignore-user-config", "--skip-git-repo-check", "--sandbox", "read-only", "--cd", workDir,
                     "--output-last-message", lastMessagePath })
            psi.ArgumentList.Add(a);
        string selectedModel = ModelFor(action);
        if (!string.IsNullOrWhiteSpace(selectedModel)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(selectedModel); }
        Config(psi, "approval_policy", "'never'");
        Config(psi, "model_instructions_file", TomlLiteral(instructionsPath));
        Config(psi, "model_reasoning_effort", TomlLiteral(ReasoningFor(action)));
        Config(psi, "project_doc_max_bytes", "0");
        Config(psi, "web_search", "'disabled'");
        Config(psi, "features.shell_tool", "false");

        // The game's MCP server — only if the action needs tools; the server itself exposes the set (--tools).
        if (AiActionTools.ToolsFor(action).Length > 0)
        {
            var args = AiActionTools.McpArgs(action, logger, withToolFilter: true);
            Config(psi, "mcp_servers.navidnd.command", TomlLiteral(config.McpServerPath));
            Config(psi, "mcp_servers.navidnd.args", "[" + string.Join(",", args.Select(TomlLiteral)) + "]");
            Config(psi, "mcp_servers.navidnd.required", "true");
            // The player has already allowed the DM to use the game's tools; the CLI runs without the approvals dialog.
            foreach (string tool in AiActionTools.ToolsFor(action))
                Config(psi, $"mcp_servers.navidnd.tools.{tool}.approval_mode", "'approve'");
            Config(psi, "mcp_servers.navidnd.startup_timeout_sec", "30");
            // ask_player/roll_dice wait for the player (up to 50 s), plan_location builds a location — with margin.
            Config(psi, "mcp_servers.navidnd.tool_timeout_sec", "180");
        }
        psi.ArgumentList.Add("-");   // prompt comes from stdin
        return psi;
    }

    private string ReasoningFor(string action) => action is "DescribeHero" or "RepairJson" ? "low"
        : config.CodexReasoningEffort is "low" or "medium" or "high" ? config.CodexReasoningEffort : "medium";

    private string ModelFor(string action)
    {
        string selected = action switch
        {
            "CreateNewGame" or "DescribeHero" => config.CodexCreateNewGameModel,
            "CreateWorld" or "StartNewGame" => config.CodexStartNewGameModel,
            _ => config.CodexModel,
        };
        return string.IsNullOrWhiteSpace(selected) ? config.CodexModel : selected;
    }

    // Use the same bounded discovery as startup, including repair of stale absolute paths.
    private static string ResolveCodex(string path) =>
        AiCliDiscovery.Resolve("codex", path) ?? AiCliDiscovery.NormalizePath(path);

    private static void Config(ProcessStartInfo psi, string key, string tomlValue)
    {
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"{key}={tomlValue}");
    }

    // JSON string escaping is compatible with TOML basic strings: preserves both paths and apostrophes.
    private static string TomlLiteral(string s) => JsonSerializer.Serialize(s);

    // A `codex exec --json` event (a JSON line): agent message text and/or an error. Both known
    // formats are supported: the new one ({"type":"item.completed","item":{"type":"agent_message","text":…}}, "turn.failed", "error")
    // and the old one ({"msg":{"type":"agent_message","message":…}} / {"msg":{"type":"error",…}}).
    private static (string? text, string? error) ParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            string type = Str(root, "type") ?? "";
            if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                && Str(item, "type") is "agent_message" or "assistant_message")
                return (Str(item, "text"), null);
            if (type == "turn.failed" && root.TryGetProperty("error", out var te))
                return (null, te.ValueKind == JsonValueKind.Object ? Str(te, "message") ?? te.ToString() : te.ToString());
            if (type == "error") return (null, Str(root, "message") ?? line);
            if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                string mtype = Str(msg, "type") ?? "";
                if (mtype == "agent_message") return (Str(msg, "message"), null);
                if (mtype is "error" or "stream_error") return (null, Str(msg, "message") ?? msg.ToString());
            }
        }
        catch (JsonException) { /* not JSON — a log line */ }
        return (null, null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // Response in ```json … ``` — strip the fence (as with the Claude CLI).
    private static string StripMarkdownFences(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```"))
        {
            int newline = s.IndexOf('\n');
            if (newline >= 0) s = s[(newline + 1)..];
        }
        if (s.EndsWith("```")) s = s[..^3];
        return s.Trim();
    }
}

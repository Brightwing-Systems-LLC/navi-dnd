namespace NaviDnD;

public class AppConfig
{
    public static string ProjectRoot { get; } = FindProjectRoot();

    private static string FindProjectRoot()
    {
        if (Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts")))
            return AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.csproj").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public static string AssetDirectory(string name)
    {
        string bundled = Path.Combine(ProjectRoot, name);
        return Directory.Exists(bundled) ? bundled : Path.GetFullPath(Path.Combine(ProjectRoot, "..", name));
    }

    public string RuleSet { get; init; } = "Dnd5e";

    // Interface language: "ru" or "en" (L.SetLanguage). The world language is separate, set in the world at creation. A new install
    // (no settings file yet) — English; settings from an older version without the field — Russian (LoadUserSettings).
    public string Language { get; set; } = L.English;

    // Claude CLI
    public string ClaudeOAuthToken { get; set; } = "";
    public string ClaudeCliPath { get; set; } = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
    public string ClaudeModel { get; set; } = "claude-opus-5-5"; // "claude-sonnet-5"; //"claude-haiku-4-5-20251001"; //"claude-sonnet-4-6";
    public string ClaudeCreateNewGameModel { get; set; } = "claude-opus-5-5";
    public string ClaudeStartNewGameModel { get; set; } = "claude-opus-5-5";

    // AI backend: "claude" — Claude CLI (default), "codex" — OpenAI Codex CLI (CodexCliAiProvider).
    public string AiProvider { get; set; } = "claude";
    // Path to Codex CLI: "codex" — look it up in PATH (codex.exe itself is taken from the npm package), otherwise a full path.
    public string CodexCliPath { get; set; } = "codex";
    // Codex model for all game requests; empty means Codex's default model.
    public string CodexModel { get; set; } = "gpt-6.1-sol";
    public string CodexCreateNewGameModel { get; set; } = "gpt-6.1-sol";
    public string CodexStartNewGameModel { get; set; } = "gpt-6.1-sol";
    // Separate from the Codex settings used for development: medium balances speed and adherence to the game rules.
    public string CodexReasoningEffort { get; set; } = "medium";

    public string McpServerPath { get; init; } = FindMcpServer();

    // The apphost: NaviDnD.McpServer.exe on Windows, NaviDnD.McpServer (no extension) on macOS/Linux.
    private static string FindMcpServer()
    {
        string executable = OperatingSystem.IsWindows() ? "NaviDnD.McpServer.exe" : "NaviDnD.McpServer";
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string bundled = Path.Combine(baseDir, executable);
        if (File.Exists(bundled)) return bundled;
        string configuration = new DirectoryInfo(baseDir).Parent?.Name ?? "Debug";
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
            "NaviDnD.McpServer", "bin", configuration, "net9.0", executable));
    }

    public void InitializeStorage()
    {
        foreach (string folder in new[] { "Storage", "Storage/Worlds", "Storage/Heroes", "Storage/Portraits", "logs" })
            Directory.CreateDirectory(Path.Combine(ProjectRoot, folder));
        if (!File.Exists(UserSettingsPath))
        {
            SaveUserSettings();
        }
    }

    public Task<(string? claude, string? codex)> DiscoverAiPathsAsync()
    {
        string claude = ClaudeCliPath, codex = CodexCliPath;
        return Task.Run(() => (Helpers.AiCliDiscovery.Resolve("claude", claude), Helpers.AiCliDiscovery.Resolve("codex", codex)));
    }

    public bool ApplyAiPaths(string? claude, string? codex, bool chooseProvider)
    {
        bool changed = false;
        if (claude != null && ClaudeCliPath != claude) { ClaudeCliPath = claude; changed = true; }
        if (codex != null && CodexCliPath != codex) { CodexCliPath = codex; changed = true; }
        if (chooseProvider && claude == null && codex != null && AiProvider != "codex") { AiProvider = "codex"; changed = true; }
        return changed;
    }

    // Debug
    public bool DisableTriggers { get; set; } = false;
    public bool RevealMap { get; set; } = false;
    // AI errors (malformed response, failed call) shown in detail in the dialogue; when off, a short "repeat the action".
    public bool ShowAiErrors { get; set; } = false;
    // Model for repairing broken JSON in the DM's response (a short call without tools).
    public string RepairJsonModel { get; set; } = "claude-haiku-4-5-20251001";

    // Whether to keep explored cells (fog of war) between game runs. Kept by default.
    public bool SaveExploredCells { get; set; } = true;
    // Key hints "[F1]", "[Esc]" in headers and sub-tabs; when off, their place is blank.
    public bool ShowKeyHints { get; set; } = true;
    // Full screen: the game window at the same size centred on the monitor, surrounded by the game background (Helpers/FullscreenBackdrop).
    public bool Fullscreen { get; set; } = true;
    // Console font (the "FONT FACE" setting).
    public string FontFace { get; set; } = "Consolas";
    // Full-screen font size: 0 — auto (the largest at which the game fits the monitor), otherwise the size.
    public int FullscreenFontSize { get; set; } = 0;
    // Click sound for buttons/arrows (Helpers/Sound.cs).
    public bool SoundEnabled { get; set; } = true;
    // Typing sound for AI text in the dialogue (synthesised in Sound.PlayTyping, works only with SoundEnabled).
    public bool TypingSoundEnabled { get; set; } = true;
    // Background music (Helpers/Music) and its volume 0–100 % (multiplied by the master volume).
    public bool MusicEnabled { get; set; } = true;
    public int MusicVolume { get; set; } = 30;
    public bool AmbienceEnabled { get; set; } = true;   // ambient sounds (birds, fireplace, dripping)
    // Master sound volume, 0–100 % (Sound.Volume).
    public int SoundVolume { get; set; } = 20;
    public bool SpeechEnabled { get; set; } = false;
    public int SpeechVolume { get; set; } = 70;
    public string SileroVoice { get; set; } = "baya";
    // Narrator voice for an English game (Speech.EnglishVoices; model: Speech/models/v3_en.pt).
    public string SileroVoiceEn { get; set; } = "en_57";
    public string SileroPythonPath { get; set; } = "python";
    // [] = off, ["*"] = all actions, or any subset of:
    // "CreateNewGame", "FixHeroForNewGame", "StartNewGame", "SendAction"
    // Via the NAVIDND_TEST_MOCKED_ACTIONS env var (a comma-separated list) — for E2E tests,
    // so they do not hit the real AI (see NaviDnD.UiTests/GameSession.cs).
    public string[] MockedActions { get; init; } = ParseMockedActions();

    // Overrides the base folder SelectiveMockProvider reads response.json from —
    // by default it is Prompts/{RuleSet}/{Action}/, which would touch the production files in the repository.
    public string? MockResponsesDir { get; init; } = Environment.GetEnvironmentVariable("NAVIDND_TEST_MOCK_RESPONSES_DIR")
        ?? (TestMocksSet ? null : DevMocksDir);

    // Debugging in a normal run (without the test env var): these actions do not call the AI but answer from
    // Storage/DevMocks/{Action}/response.json. CreateNewGame gives a ready-made hero, to test map generation/
    // game start without creating a character every time. [] turns it off.
    public static readonly string[] DevMockedActions = [];
    public static string DevMocksDir => Path.Combine(ProjectRoot, "Storage", "DevMocks");

    // ── Player settings (the "SETTINGS" screen) ──────────────────────────────────
    // Saved to Storage/settings.json (in .gitignore — it holds the token) on top of the defaults above.
    public static string UserSettingsPath => Path.Combine(ProjectRoot, "Storage", "settings.json");

    private sealed class UserSettings
    {
        public bool? SoundEnabled { get; set; }
        public bool? TypingSoundEnabled { get; set; }
        public int? SoundVolume { get; set; }
        public bool? SpeechEnabled { get; set; }
        public int? SpeechVolume { get; set; }
        public string? SileroVoice { get; set; }
        public string? SileroVoiceEn { get; set; }
        public string? SileroPythonPath { get; set; }
        public bool? MusicEnabled { get; set; }
        public int? MusicVolume { get; set; }
        public bool? AmbienceEnabled { get; set; }
        public string? ClaudeModel { get; set; }
        public string? ClaudeCreateNewGameModel { get; set; }
        public string? ClaudeStartNewGameModel { get; set; }
        public string? ClaudeOAuthToken { get; set; }
        public string? ClaudeCliPath { get; set; }
        public string? AiProvider { get; set; }
        public string? CodexCliPath { get; set; }
        public string? CodexModel { get; set; }
        public string? CodexCreateNewGameModel { get; set; }
        public string? CodexStartNewGameModel { get; set; }
        public string? CodexReasoningEffort { get; set; }
        public bool? SaveExploredCells { get; set; }
        public bool? RevealMap { get; set; }
        public bool? ShowAiErrors { get; set; }
        public bool? DisableTriggers { get; set; }
        public bool? ShowKeyHints { get; set; }
        public bool? Fullscreen { get; set; }
        public int? FullscreenFontSize { get; set; }
        public string? FontFace { get; set; }
        public string? Language { get; set; }
    }

    public void LoadUserSettings()
    {
        try
        {
            if (!File.Exists(UserSettingsPath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(UserSettingsPath));
            if (s == null) return;
            SoundEnabled = s.SoundEnabled ?? SoundEnabled;
            TypingSoundEnabled = s.TypingSoundEnabled ?? TypingSoundEnabled;
            SoundVolume = Math.Clamp(s.SoundVolume ?? SoundVolume, 0, 100);
            SpeechEnabled = s.SpeechEnabled ?? SpeechEnabled;
            SpeechVolume = Math.Clamp(s.SpeechVolume ?? SpeechVolume, 0, 100);
            SileroVoice = s.SileroVoice is "eugene" or "aidar" or "baya" or "kseniya" or "xenia" ? s.SileroVoice : SileroVoice;
            SileroVoiceEn = s.SileroVoiceEn is { } en && Helpers.Speech.EnglishVoices.Contains(en) ? en : SileroVoiceEn;
            SileroPythonPath = s.SileroPythonPath ?? SileroPythonPath;
            MusicEnabled = s.MusicEnabled ?? MusicEnabled;
            MusicVolume = Math.Clamp(s.MusicVolume ?? MusicVolume, 0, 100);
            AmbienceEnabled = s.AmbienceEnabled ?? AmbienceEnabled;
            ClaudeModel = s.ClaudeModel ?? ClaudeModel;
            ClaudeCreateNewGameModel = s.ClaudeCreateNewGameModel ?? ClaudeCreateNewGameModel;
            ClaudeStartNewGameModel = s.ClaudeStartNewGameModel ?? ClaudeStartNewGameModel;
            ClaudeOAuthToken = s.ClaudeOAuthToken ?? ClaudeOAuthToken;
            ClaudeCliPath = s.ClaudeCliPath ?? ClaudeCliPath;
            AiProvider = s.AiProvider ?? AiProvider;
            CodexCliPath = s.CodexCliPath ?? CodexCliPath;
            CodexModel = s.CodexModel ?? CodexModel;
            CodexCreateNewGameModel = s.CodexCreateNewGameModel ?? CodexModel;
            CodexStartNewGameModel = s.CodexStartNewGameModel ?? CodexModel;
            CodexReasoningEffort = s.CodexReasoningEffort is "low" or "medium" or "high"
                ? s.CodexReasoningEffort : CodexReasoningEffort;
            SaveExploredCells = s.SaveExploredCells ?? SaveExploredCells;
            RevealMap = s.RevealMap ?? RevealMap;
            ShowAiErrors = s.ShowAiErrors ?? ShowAiErrors;
            DisableTriggers = s.DisableTriggers ?? DisableTriggers;
            ShowKeyHints = s.ShowKeyHints ?? ShowKeyHints;
            Fullscreen = s.Fullscreen ?? Fullscreen;
            FullscreenFontSize = s.FullscreenFontSize ?? FullscreenFontSize;
            FontFace = s.FontFace ?? FontFace;
            // No field — settings from a version before language selection: the player played in Russian, and keeps doing so after the update.
            Language = L.Normalize(s.Language ?? L.Russian);
        }
        catch { /* broken settings file — the defaults remain */ }
    }

    public void SaveUserSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath)!);
            var s = new UserSettings
            {
                SoundEnabled = SoundEnabled, TypingSoundEnabled = TypingSoundEnabled, SoundVolume = SoundVolume,
                SpeechEnabled = SpeechEnabled, SpeechVolume = SpeechVolume,
                SileroVoice = SileroVoice, SileroVoiceEn = SileroVoiceEn, SileroPythonPath = SileroPythonPath,
                MusicEnabled = MusicEnabled, MusicVolume = MusicVolume, AmbienceEnabled = AmbienceEnabled,
                ClaudeModel = ClaudeModel, ClaudeCreateNewGameModel = ClaudeCreateNewGameModel,
                ClaudeStartNewGameModel = ClaudeStartNewGameModel, ClaudeOAuthToken = ClaudeOAuthToken,
                ClaudeCliPath = ClaudeCliPath, SaveExploredCells = SaveExploredCells, RevealMap = RevealMap, ShowAiErrors = ShowAiErrors,
                AiProvider = AiProvider, CodexCliPath = CodexCliPath, CodexModel = CodexModel, Fullscreen = Fullscreen,
                CodexReasoningEffort = CodexReasoningEffort,
                CodexCreateNewGameModel = CodexCreateNewGameModel, CodexStartNewGameModel = CodexStartNewGameModel,
                FullscreenFontSize = FullscreenFontSize, FontFace = FontFace,
                DisableTriggers = DisableTriggers, ShowKeyHints = ShowKeyHints, Language = Language,
            };
            File.WriteAllText(UserSettingsPath, System.Text.Json.JsonSerializer.Serialize(s,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not saved — the settings apply until the end of the session */ }
    }

    private static bool TestMocksSet => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NAVIDND_TEST_MOCKED_ACTIONS"));

    private static string[] ParseMockedActions()
    {
        string? v = Environment.GetEnvironmentVariable("NAVIDND_TEST_MOCKED_ACTIONS");
        return string.IsNullOrEmpty(v) ? DevMockedActions : v.Split(',');
    }
}

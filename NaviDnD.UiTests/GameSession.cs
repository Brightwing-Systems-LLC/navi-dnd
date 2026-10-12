using System.Diagnostics;

namespace NaviDnD.UiTests;

// Launches the REAL NaviDnD.exe (no changes to game logic, apart from the test entry points —
// NAVIDND_TEST_WORLDSTATE/NAVIDND_TEST_MOCKED_ACTIONS/NAVIDND_TEST_MOCK_RESPONSES_DIR, see
// the game's own Program.cs/AppConfig.cs) with the state from the given fixture and waits for the Menu screen.
// Every test case creates ITS OWN independent instance — game processes do not depend on each other
// and are not reused between cases.
internal sealed class GameSession : IDisposable
{
    public int Pid { get; }

    // The real (production, not test) NaviDnD/Storage folder — needed by tests that themselves
    // inject roll_request.json/ask_player_request.json to trigger the roll/question dialog
    // without a real MCP tool (see GameAiClient.SpinAsync — it polls these files regardless
    // of whether the call is mocked or real).
    public string RealStorageDir { get; }

    private readonly Process _process;
    private readonly Dictionary<string, byte[]?> _realSaveBackup;
    private HashSet<string> _worldDirsBefore = [];

    private static HashSet<string> WorldDirs(string storageDir)
    {
        string dir = Path.Combine(storageDir, "Worlds");
        return Directory.Exists(dir) ? [.. Directory.GetDirectories(dir)] : [];
    }

    private GameSession(Process process, Dictionary<string, byte[]?> realSaveBackup, string realStorageDir)
    {
        _process = process;
        Pid = process.Id;
        _realSaveBackup = realSaveBackup;
        RealStorageDir = realStorageDir;
    }

    public static GameSession Launch(
        string fixtureFileName = "full_game.json", string[]? mockedActions = null, string mockResponsesFolder = "") =>
        LaunchInternal(fixtureFileName, mockedActions, mockResponsesFolder);

    // For "create a new hero and start the game" scenarios: enables SelectiveMockProvider
    // (CreateNewGame + FixHeroForNewGame + StartNewGame) on the canonical mocks from
    // Fixtures/MockResponses/ — the real model is never called. FixHeroForNewGame
    // is always mocked (even if a given test never calls it) — harmless, it just goes unused.
    public static GameSession LaunchForNewGame() =>
        LaunchInternal("full_game.json", ["CreateNewGame", "FixHeroForNewGame", "StartNewGame"], mockResponsesFolder: "");

    // mockedActions — see AppConfig.MockedActions ([] = off, ["*"] = all, or specific names).
    // mockResponsesFolder — a subfolder inside Fixtures/MockResponses/, so different tests don't share
    // the same response.json for the same action name (e.g. different SendAction responses
    // for the inventory test and the dice roll test).
    // Any mocked action may unconditionally write to the PRODUCTION NaviDnD/Storage/worldState.json and
    // newGameState.json (StartNewGame.Save()/SendAction.Save() etc., not to the test fixture) —
    // their contents are snapshotted byte for byte before launch and restored in Dispose().
    private static GameSession LaunchInternal(string fixtureFileName, string[]? mockedActions, string mockResponsesFolder)
    {
        SelfWindow.ApplyFixedWidth();
        SelfWindow.MoveToRightEdge();

        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);
        string gameExe = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "NaviDnD", "bin", "Debug", "net9.0", "NaviDnD.exe"));

        if (!File.Exists(gameExe))
            throw new InvalidOperationException(
                "NaviDnD.exe not found at " + gameExe + " — build the main project (NaviDnD.csproj) first.");

        string realStorageDir = Path.Combine(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gameExe)!, "..", "..", "..")), "Storage");

        // UseShellExecute=true doesn't allow setting ProcessStartInfo.EnvironmentVariables directly —
        // we set the variables on ourselves, and the child process inherits them as usual.
        Environment.SetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE", fixturePath);

        // The backup is UNCONDITIONAL, not only with mockedActions: Storage.Save() writes to this FIXED
        // path (SavePath) on ANY game event that is normally saved — including
        // plain arrow-key movement (MovementHandler.TryMove → ApplyClientMovement → Save()),
        // with no AI call at all. NAVIDND_TEST_WORLDSTATE only replaces where the game
        // LOADS from — Storage.Save() still writes to the production file. The backup used to be conditional (only
        // with mocked actions), so any test without mockedActions (movement/fog of war/round)
        // silently and IRREVERSIBLY overwrote the user's real save with no way to restore it.
        var realSaveBackup = new Dictionary<string, byte[]?>();
        foreach (string name in new[] { "worldState.json", "newGameState.json", "worldState.explored.json", "newGameState.explored.json" })
        {
            string path = Path.Combine(realStorageDir, name);
            // Bytes, not text — File.ReadAllText/WriteAllText silently strip/don't write the BOM,
            // which would turn the restore into "almost the same" instead of byte for byte.
            realSaveBackup[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        // The new game's "World" step writes the world to the library and makes it active — the active world is restored too,
        // and worlds that appeared during the test are deleted (Dispose).
        string activeWorld = Path.Combine(realStorageDir, "Worlds", "active.txt");
        realSaveBackup[activeWorld] = File.Exists(activeWorld) ? File.ReadAllBytes(activeWorld) : null;
        var worldDirsBefore = WorldDirs(realStorageDir);

        if (mockedActions is { Length: > 0 })
        {
            string mockResponsesDir = string.IsNullOrEmpty(mockResponsesFolder)
                ? Path.Combine(AppContext.BaseDirectory, "Fixtures", "MockResponses")
                : Path.Combine(AppContext.BaseDirectory, "Fixtures", "MockResponses", mockResponsesFolder);
            Environment.SetEnvironmentVariable("NAVIDND_TEST_MOCKED_ACTIONS", string.Join(",", mockedActions));
            Environment.SetEnvironmentVariable("NAVIDND_TEST_MOCK_RESPONSES_DIR", mockResponsesDir);
        }
        else
        {
            // Reset in case a previous test in this same process enabled mock mode —
            // otherwise the process environment variables would leak into the next normal run.
            Environment.SetEnvironmentVariable("NAVIDND_TEST_MOCKED_ACTIONS", null);
            Environment.SetEnvironmentVariable("NAVIDND_TEST_MOCK_RESPONSES_DIR", null);
        }

        var psi = new ProcessStartInfo
        {
            FileName = gameExe,
            UseShellExecute = true, // a separate window for the game
            WorkingDirectory = Path.GetDirectoryName(gameExe)!,
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the game process.");
        var session = new GameSession(process, realSaveBackup, realStorageDir) { _worldDirsBefore = worldDirsBefore };

        try
        {
            if (!GameConsole.WaitForRowContains(process.Id, 1, GameConsole.TitleReadWidth, "MENU", 5000))
                throw new InvalidOperationException("Timed out waiting for the Menu screen after launching NaviDnD.exe.");
            GameConsole.MoveToTopLeft(process.Id);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    public void Dispose()
    {
        // CRITICAL: Kill() only REQUESTS the process to terminate and returns immediately,
        // without waiting for it to actually stop — if the killed process was in the middle of
        // Storage.Save() at that moment (writing to the PRODUCTION NaviDnD/Storage/worldState.json), its write may
        // land on disk AFTER we restore the backup below, irreversibly overwriting
        // the user's real save with test data. WaitForExit() removes this race.
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }
        catch { /* the window may have closed on its own */ }
        _process.Dispose();

        foreach (string dir in WorldDirs(RealStorageDir).Except(_worldDirsBefore))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort */ }
        }

        foreach (var (path, original) in _realSaveBackup)
        {
            try
            {
                if (original == null) { if (File.Exists(path)) File.Delete(path); }
                else File.WriteAllBytes(path, original);
            }
            catch { /* restore is best-effort — must not fail the test on the way out */ }
        }

        // Tests that inject roll_request.json/ask_player_request.json (see InteractiveResponseTests,
        // NewGameScenarioTests) leave them answered (Answered=true) but don't delete them — the game itself
        // cleans them up only on the NEXT launch (Program.cs). We clean up right away instead of waiting for the next test.
        foreach (string name in new[] { "roll_request.json", "ask_player_request.json" })
        {
            try
            {
                string path = Path.Combine(RealStorageDir, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best-effort */ }
        }
    }
}

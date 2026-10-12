using NaviDnD.Clients;
using NaviDnD.Display;
using NaviDnD.Helpers;
using System.Text;
using NaviDnD.Data;
using NaviDnD.Data.Models;

namespace NaviDnD;

class Program
{
    static async Task Main(string[] args)
    {
        // Save migration from the update helper: no window or console, the exit code is the result.
        if (args is ["--migrate", ..])
        {
            var migrateConfig = new AppConfig();
            migrateConfig.LoadUserSettings();   // error messages in the same language as the game
            L.SetLanguage(migrateConfig.Language);
            Environment.ExitCode = Migrations.MigrateCommand.Run(args.Length > 1 ? args[1] : AppConfig.ProjectRoot);
            return;
        }
        if (ConsoleHostLauncher.TryRelaunch(args)) return;
        // An unhandled exception used to just crash the process silently (the console closes immediately,
        // .NET prints the stack to stderr, which nobody sees) — not a single line in the logs. Now the crash
        // at least lands in the same ai_*.log as the regular AI calls, where it can be analysed.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                string dir = Path.Combine(AppConfig.ProjectRoot, "logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"ai_{DateTime.Now:yyyy-MM-dd}.log");
                string text = $"=== {DateTime.Now:HH:mm:ss.fff} | UNHANDLED CRASH ===\n{e.ExceptionObject}\n\n";
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            catch { /* the crash logger must not crash the crash handler itself */ }
        };

        // macOS/Linux: take over the terminal (raw input, mouse, alternate screen) before anything is drawn.
        UnixTerminal.Start();
        // Windows creates and shows the console window BEFORE the first line of Main() — hide it right away,
        // configure everything (borderless, map-sized, font/colours) invisibly and show it when ready.
        Term.CursorVisible = false;
        ConsoleSetup.BlockConsoleFullscreen();
        ConsoleSetup.LoadBundledFonts();   // fonts from the Fonts folder — before configuring the console font
        ConsoleSetup.EnableAnsiSupport();
        ConsoleSetup.DisableQuickEdit();
        ConsoleMouseReader.Enable();
        ConsoleZoomBlocker.Start(); // Ctrl+wheel / touchpad pinch do not change the font or window size
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ConsoleMouseReader.Disable();

        // The update migrates old-format saves; this is the fallback path (built from source, a failed update).
        // Not in UI tests: they have their own test state.
        if (Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE") == null)
            Migrations.MigrateCommand.RunIfNeeded(AppConfig.ProjectRoot);

        var config = new AppConfig();
        bool firstSettings = !File.Exists(AppConfig.UserSettingsPath);
        config.InitializeStorage();
        // Player settings (the "SETTINGS" screen) — not in UI tests: the defaults matter there.
        if (Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE") == null) config.LoadUserSettings();
        // UI tests (settings are not read) — in English, like their expectations.
        L.SetLanguage(Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE") == null ? config.Language : L.English);
        string initialClaudePath = config.ClaudeCliPath, initialCodexPath = config.CodexCliPath;
        string initialProvider = config.AiProvider;
        var cliDiscovery = Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE") == null
            ? config.DiscoverAiPathsAsync() : null;

        bool discoveredClientAvailable = true;
        async Task<bool> ApplyCliDiscovery()
        {
            if (cliDiscovery == null) return discoveredClientAvailable
                || File.Exists(AiCliDiscovery.NormalizePath(config.ClaudeCliPath))
                || File.Exists(AiCliDiscovery.NormalizePath(config.CodexCliPath));
            var result = await cliDiscovery;
            cliDiscovery = null;
            // Settings may have been edited while discovery was running.
            bool changed = config.ApplyAiPaths(
                config.ClaudeCliPath == initialClaudePath ? result.claude : null,
                config.CodexCliPath == initialCodexPath ? result.codex : null,
                firstSettings && config.AiProvider == initialProvider);
            if (changed) config.SaveUserSettings();
            discoveredClientAvailable = result.claude != null || result.codex != null;
            return discoveredClientAvailable;
        }
        Sound.Enabled = config.SoundEnabled;
        Speech.Initialize(config);
        GameUpdates.Initialize();
        TempCleanup.Run();
        Sound.Volume = config.SoundVolume / 100f;
        Sound.TypingEnabled = config.TypingSoundEnabled;
        Music.Enabled = config.MusicEnabled;
        Music.AmbienceEnabled = config.AmbienceEnabled;
        // NAVIDND_MUTE=1: silence for development runs (fixture runs skip the player's settings, so sounds are on there).
        if (Environment.GetEnvironmentVariable("NAVIDND_MUTE") == "1")
            Sound.Enabled = Sound.TypingEnabled = Music.Enabled = Music.AmbienceEnabled = false;
        Music.Volume = config.MusicVolume / 100f;

        var storage = new Storage { SaveExploredCells = config.SaveExploredCells };
        string? testStatePath = Environment.GetEnvironmentVariable("NAVIDND_TEST_WORLDSTATE");
        Func<bool> hasSave = testStatePath != null ? () => File.Exists(testStatePath) : Storage.HasSave;
        // Games started before the world map existed are attached to the active world on load (GameWorld.EnsureLink).
        Action loadCurrentState = testStatePath != null ? () => storage.LoadFrom(testStatePath) : () => { storage.LoadSave(); GameWorld.EnsureLink(storage.WorldState); };
        Storage.InitGames(); // the old save → game 1, the active game is the latest
        if (hasSave()) loadCurrentState();
        Speech.WarmUp();   // preload the model for the last game's language (its language is known after loading)
        var settings = storage.WorldState;

        ConsoleSetup.ShowWindow(); // it does not open without this
        // Full screen and scale (settings; not in UI tests) — before the first window setup: a font that fits the monitor.
        ConsoleSetup.Fullscreen = config.Fullscreen && testStatePath == null;
        ConsoleSetup.FullscreenFontSize = config.FullscreenFontSize;
        // The font from settings (UI tests use the default); one removed from the list (SimSun, MS Gothic, Lucida Console, Cascadia Mono, Courier New, Unifont) falls back to Consolas.
        if (testStatePath == null)
            ConsoleSetup.FontFace = config.FontFace = ConsoleSetup.FontFaces.Contains(config.FontFace) ? config.FontFace : "Consolas";
        ConsoleSetup.SetConsoleConfig(settings);
        var logger = new AiLogger();

        // Clean up stale requests from previous session
        string rollRequestPath = Path.Combine(AppConfig.ProjectRoot, "Storage", "roll_request.json");
        if (File.Exists(rollRequestPath)) File.Delete(rollRequestPath);
        string askPlayerRequestPath = Path.Combine(AppConfig.ProjectRoot, "Storage", "ask_player_request.json");
        if (File.Exists(askPlayerRequestPath)) File.Delete(askPlayerRequestPath);
        string selectTargetRequestPath = Path.Combine(AppConfig.ProjectRoot, "Storage", "select_target_request.json");
        if (File.Exists(selectTargetRequestPath)) File.Delete(selectTargetRequestPath);

        // The AI comes from settings: Claude CLI or Codex CLI (the same prompt and the game's MCP server), switching takes effect immediately.
        IAiProvider provider = new SwitchableAiProvider(config, logger);
        if (config.MockedActions.Length > 0)
            provider = new SelectiveMockProvider(provider, config.MockedActions.ToHashSet(), config.MockResponsesDir);

        var aiClient = new GameAiClient(settings, storage, provider, config) { Logger = logger };

        var display = new DisplayConfig { ShowKeyHints = config.ShowKeyHints };
        // Full screen (a setting; not in UI tests) — the window already has its size: the backdrop and the window centred.
        if (config.Fullscreen && testStatePath == null) FullscreenBackdrop.Set(true, display.MainBackground);
        var borderDrawer = new BorderDrawer(settings, display);
        var legendDisplay = new LegendDisplay(storage, display, config);
        var mapDisplay = new MapDisplay(settings, display, legendDisplay);
        var heroDisplay = new HeroDisplay(settings, display);
        var abilityDisplay = new AbilityDisplay(settings, display);
        var levelUpDisplay = new LevelUpDisplay(settings, display, aiClient, storage);
        var journalDisplay = new JournalDisplay(settings, display);
        // What has already been seen in the journal (for the "JOURNAL•" badge): a snapshot of its contents; a new/different game starts over.
        string? journalSeen = null;
        int journalSeenSlot = -1;

        // Warm up the theme background/frame BEFORE showing the window — otherwise the user sees a frame with the default
        // black background/white text instead of our styling at the moment the window appears.
        Term.CursorVisible = false;
        Console.BackgroundColor = ConsoleColor.Black;
        ColorHelper.ClearScreen(display.MainBackground, display.MainForeground);
        ColorHelper.SetBackgroundColor(display.MainBackground);
        ColorHelper.SetForegroundColor(display.MainForeground);
        Console.OutputEncoding = Encoding.UTF8;

        var mapLoop = new MapScreenLoop(settings, display, storage, aiClient, config, mapDisplay, legendDisplay);

        var state = new GameState();
        WindowControls.Start(() =>
        {
            config.Fullscreen = !config.Fullscreen;
            if (!config.Fullscreen) FullscreenBackdrop.Set(false, display.MainBackground);
            ConsoleSetup.Fullscreen = config.Fullscreen;
            ConsoleSetup.SetConsoleConfig(settings);
            if (config.Fullscreen) FullscreenBackdrop.Set(true, display.MainBackground);
            config.SaveUserSettings();
        }, () =>
        {
            if (state.NewGame != null)
            {
                if (state.NewGame.Wizard != null && !state.NewGame.Wizard.ConfirmExit().GetAwaiter().GetResult()) return;
            }
            else if (settings.Hero != null)
            {
                if (testStatePath == null) storage.Save();
            }
            Speech.Stop();
            FullscreenBackdrop.Set(false, display.MainBackground);
            ConsoleSetup.CloseConsoleAndExit();
        });
        // Music and ambient sounds to suit the situation (MusicDirector, sound/music, sound/ambience). Silence in UI tests.
        if (testStatePath == null) MusicDirector.Start(state, settings);
        var screens = ScreenRegistry.GetScreens(state, storage, hasSave, loadCurrentState, () => GameWorld.HasLocation(settings));

        // Install before the first streaming tab switch, even if these screens were never visited.
        void ScrollCharacter(int delta)
        {
            switch (display.CharacterSubTab)
            {
                case CharacterSubTab.Inventory: display.InventoryScrollOffset = Math.Max(0, display.InventoryScrollOffset + delta); break;
                case CharacterSubTab.Abilities: display.AbilityPageOffset = Math.Max(0, display.AbilityPageOffset + delta); break;
                case CharacterSubTab.Spells: display.SpellsPageOffset = Math.Max(0, display.SpellsPageOffset + delta); break;
                default: display.EffectsPageOffset = Math.Max(0, display.EffectsPageOffset + delta); break;
            }
        }
        display.JournalPollActionFactory = dlg => MouseUiHelper.MakeJournalPollAction(
            (DialogDisplay)dlg, journalDisplay, screens[Screen.Journal].Title, display);
        display.CharacterPollActionFactory = dlg => MouseUiHelper.MakeCharacterPollAction(
            (DialogDisplay)dlg, heroDisplay, 2, heroDisplay.DrawHeroCard,
            () => ScrollCharacter(-1), () => ScrollCharacter(1),
            screens[Screen.Character].Title, MouseUiHelper.ComputeTitleTabs(screens[Screen.Character].Title), display,
            (idx, page) => { var items = settings.Hero?.Inventory; if (items != null && idx >= 0 && idx < items.Count) MouseUiHelper.SetSelectedInventoryItem(display, items[idx], page); },
            (idx, page) => { var spells = settings.Hero == null ? [] : HeroDisplay.SortedSpells(settings.Hero); if (idx >= 0 && idx < spells.Count) MouseUiHelper.SetSelectedSpell(display, spells[idx], page); });

        display.TabSwitchProvider = tabKey => tabKey switch
        {
            // Map (terrain) only when there is a tactical map; without one the tab is greyed out.
            "F1" => display.OnMapRedraw == null || !GameWorld.HasLocation(settings) ? (null, null) : (() => { display.JournalShown = false; display.MapLevel = MapLevel.Location; display.OnMapRedraw(); }, screens[Screen.Map].Title),
            "F2" => display.OnMapRedraw == null ? (null, null) : (() => { display.JournalShown = false; display.MapLevel = MapLevel.World; display.OnMapRedraw(); }, screens[Screen.World].Title),
            "F3" => (() => { display.JournalShown = true; journalSeen = JournalDisplay.Stamp(settings); journalDisplay.Draw(); }, screens[Screen.Journal].Title),
            "F4" => (() => { display.JournalShown = false; heroDisplay.DrawHeroCard(); }, screens[Screen.Character].Title),
            "F5" => (() => { display.JournalShown = false; abilityDisplay.Draw(); }, screens[Screen.Abilities].Title),
            "F6" when display.JournalShown => (() => { journalDisplay.SetSectionSilently(JournalDisplay.Section.Quests); journalDisplay.Draw(); }, screens[Screen.Journal].Title),
            "F7" when display.JournalShown => (() => { journalDisplay.SetSectionSilently(JournalDisplay.Section.People); journalDisplay.Draw(); }, screens[Screen.Journal].Title),
            "F8" when display.JournalShown => (() => { journalDisplay.SetSectionSilently(JournalDisplay.Section.Locations); journalDisplay.Draw(); }, screens[Screen.Journal].Title),
            "F9" when display.JournalShown => (() => { journalDisplay.SetSectionSilently(JournalDisplay.Section.Bestiary); journalDisplay.Draw(); }, screens[Screen.Journal].Title),
            "F10" when display.JournalShown => (() => { journalDisplay.SetSectionSilently(JournalDisplay.Section.Notes); journalDisplay.Draw(); }, screens[Screen.Journal].Title),
            "F6" => (() => { display.CharacterSubTab = CharacterSubTab.Inventory;  display.InventoryScrollOffset = 0; display.SelectedInventoryIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; heroDisplay.DrawHeroCard(); }, screens[Screen.Character].Title),
            "F7" => (() => { display.CharacterSubTab = CharacterSubTab.Abilities; display.AbilityPageOffset  = 0; display.SelectedInventoryIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; heroDisplay.DrawHeroCard(); }, screens[Screen.Character].Title),
            "F8" => (() => { display.CharacterSubTab = CharacterSubTab.Effects;   display.EffectsPageOffset  = 0; display.SelectedInventoryIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; heroDisplay.DrawHeroCard(); }, screens[Screen.Character].Title),
            "F9" => !heroDisplay.HasSpells ? (null, null) : (() => { display.CharacterSubTab = CharacterSubTab.Spells;    display.SpellsPageOffset   = 0; display.SelectedInventoryIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; heroDisplay.DrawHeroCard(); }, screens[Screen.Character].Title),
            _    => (null, null)
        };

        // "MY GAMES": pick a game (load it and go to the map) or a free slot (a new game goes there).
        void ShowGames(string? message)
        {
            ColorHelper.ClearScreen(display.MainBackground, display.MainForeground);
            borderDrawer.DrawTopBorder();
            string gamesTitle = L.T(" MY GAMES   [Esc]MENU");
            borderDrawer.DrawContentLine(() => MouseUiHelper.WriteColoredTitle(gamesTitle, display));
            var (action, slot) = new GamesDisplay(settings, display, gamesTitle, message).Show();
            if (action == GamesDisplay.Action.Play)
            {
                Storage.SetActiveSlot(slot);
                loadCurrentState();
                state.CurrentScreen = Screen.Map;
            }
            else if (action == GamesDisplay.Action.NewGame)
            {
                state.NewGameSlot = slot;
                state.NewGame = null;   // start creation from scratch
                state.CurrentScreen = Screen.NewGame;
            }
        }

        while (true)
        {
            if (cliDiscovery != null && (cliDiscovery.IsCompleted || state.CurrentScreen != Screen.Menu))
                await ApplyCliDiscovery();
            if (state.PendingStartNewGame)
            {
                state.PendingStartNewGame = false;
                // The new game is saved to the slot chosen for it (Storage.SlotPath) — it becomes the current one.
                if (state.NewGameSlot is int newSlot) Storage.SetActiveSlot(newSlot);
                state.NewGameSlot = null;
                var startTask = aiClient.StartNewGame("");
                // Waiting gets its own screen: a frame, a title and a centred message (rather than in a corner of the previous screen).
                ColorHelper.ClearScreen(display.MainBackground, display.MainForeground);
                Term.SetCursorPosition(0, 0);
                borderDrawer.DrawTopBorder();
                borderDrawer.DrawContentLine(() => MouseUiHelper.WriteColoredTitle(L.T(" NEW GAME"), display));
                borderDrawer.DrawSeparator();
                // The frame reaches the window's last row, as on other screens: top, title, divider and bottom — 4 rows.
                int waitRows = Math.Max(3, Term.WindowHeight - 4);
                for (int r = 0; r < waitRows; r++) borderDrawer.DrawContentLine(() => { });
                borderDrawer.DrawBottomBorder();
                await Helpers.Spinner.WhileCentered(startTask, L.T("Creating the world"), 3 + waitRows / 2,
                    DisplayConfig.LeftMargin + 1, display.InnerWidth(display.ViewCols(settings.Map)), ColorHelper.Pale(display.MainForeground, 0.75));
                await startTask;
                // Adventure parameters go to the DM every turn (narrative.style → the style: line in the state).
                if (storage.NewGameData?.AdventureStyle is { Length: > 0 } adventureStyle)
                {
                    (storage.WorldState.Narrative ??= new()).Style = adventureStyle;
                    storage.Save();
                }
                // The hero goes into the hero library (a new game with them — the "Hero" step).
                // (not in UI tests: the test hero must not end up in the player's list)
                if (testStatePath == null) HeroLibrary.SyncFromSave(Storage.SavePath);
                logger.LogTriggers(storage.WorldState.Map);
            }

            ConsoleSetup.SetConsoleConfig(settings);
            Term.CursorVisible = false;
            Console.BackgroundColor = ConsoleColor.Black;
            ColorHelper.ClearScreen(display.MainBackground, display.MainForeground);
            ColorHelper.SetBackgroundColor(display.MainBackground);
            ColorHelper.SetForegroundColor(display.MainForeground);
            Console.OutputEncoding = Encoding.UTF8;

            // No tactical map (the hero is travelling, a text scene) — show the world instead of terrain (before drawing the header).
            if (state.CurrentScreen == Screen.Map && !GameWorld.HasLocation(settings)) state.CurrentScreen = Screen.World;
            var screen = screens[state.CurrentScreen];

            // The main menu draws the whole screen itself (logo, items, pictures) — without a header line.
            // F1/F2/Esc are the same commands as the Menu screen; ActiveTabKey is reset so the sound is not muted.
            if (state.CurrentScreen == Screen.Menu)
            {
                display.ActiveTabKey = null;
                var choice = new MainMenuDisplay(settings, display).Show(hasSave());
                if (choice is MainMenuDisplay.Choice.Continue or MainMenuDisplay.Choice.NewGame)
                {
                    if (!await ApplyCliDiscovery())
                    {
                        AiSetupNotice.Show();
                        ColorHelper.ClearScreen(display.MainBackground, display.MainForeground);
                        borderDrawer.DrawTopBorder();
                        static string SetupTitle() => L.T(" SETTINGS: Claude and Codex not found   [Esc]MENU");
                        borderDrawer.DrawContentLine(() => MouseUiHelper.WriteColoredTitle(SetupTitle(), display));
                        new SettingsDisplay(settings, display, config, storage, SetupTitle).Show();
                        continue;
                    }
                }
                else if (cliDiscovery?.IsCompleted == true) await ApplyCliDiscovery();
                if (choice == MainMenuDisplay.Choice.Updates)
                {
                    if (await GameUpdates.ShowAsync(new UpdateDisplay(settings, display))) return;
                    continue;
                }
                if (choice == MainMenuDisplay.Choice.Settings)
                {
                    // Settings are their own framed screen with a header; leaving them returns to the menu.
                    ColorHelper.ClearScreen(display.MainBackground, display.MainForeground);
                    borderDrawer.DrawTopBorder();
                    static string SettingsTitle() => L.T(" SETTINGS   [Esc]MENU");
                    borderDrawer.DrawContentLine(() => MouseUiHelper.WriteColoredTitle(SettingsTitle(), display));
                    new SettingsDisplay(settings, display, config, storage, SettingsTitle).Show();
                    continue;
                }
                // A new game goes into the first free slot; if all are taken — "MY GAMES" with a hint to free one.
                if (choice == MainMenuDisplay.Choice.NewGame)
                {
                    state.NewGame = null;   // start creation from scratch
                    if (Storage.FreeSlot() is int free) state.NewGameSlot = free;
                    else { ShowGames(L.F("All {0} slots are taken — delete a game [Del] to start a new one", Storage.MaxGames)); continue; }
                }
                if (choice == MainMenuDisplay.Choice.Games) { ShowGames(null); continue; }
                string command = choice switch
                {
                    MainMenuDisplay.Choice.Continue => "F1",
                    MainMenuDisplay.Choice.NewGame => "F2",
                    _ => "Esc",
                };
                if (screen.Commands.TryGetValue(command, out var menuAction)) menuAction();
                continue;
            }

            borderDrawer.DrawTopBorder();

            // The journal's "new" badge appears in all in-game headers.
            if (journalSeenSlot != Storage.ActiveSlot || journalSeen == null)
            {
                journalSeenSlot = Storage.ActiveSlot;
                journalSeen = JournalDisplay.Stamp(settings);
            }
            if (state.CurrentScreen == Screen.Journal) journalSeen = JournalDisplay.Stamp(settings);
            ScreenRegistry.RefreshGameTitles(screens, JournalDisplay.Stamp(settings) != journalSeen, mapDisabled: !GameWorld.HasLocation(settings));
            // New game: the session comes before drawing the header (on the first step only "WORLD" is available).
            if (state.CurrentScreen == Screen.NewGame) state.NewGame ??= new NewGameSession();
            if (state.NewGame is { } newGameSession) ScreenRegistry.RefreshNewGameTitles(screens, newGameSession);

            string title = state.CurrentScreen == Screen.Menu && !hasSave()
                ? L.T(" MENU   [F2]NEW GAME     [Esc]QUIT")
                : screen.Title;

            borderDrawer.DrawContentLine(() => MouseUiHelper.WriteColoredTitle(title, display));

            
            switch (state.CurrentScreen)
            {
                // Map (terrain) and World share the same map loop; with no tactical map (the hero is travelling, a text
                // scene) — the world instead of terrain.
                case Screen.Map:
                case Screen.World:
                    if (state.CurrentScreen == Screen.Map && !GameWorld.HasLocation(settings))
                    {
                        state.CurrentScreen = Screen.World;
                        screen = screens[Screen.World];
                        title = screen.Title;
                    }
                    display.JournalShown = false;
                    display.ActiveTabKey = state.CurrentScreen == Screen.World ? "F2" : "F1";
                    display.MapLevel = state.CurrentScreen == Screen.World ? MapLevel.World : MapLevel.Location;
                    await mapLoop.RunAsync(screen, title);
                    break;

                case Screen.Journal:
                    display.ActiveTabKey = "F3";
                    display.JournalShown = true;
                    display.SelectedImageLines = null;
                    display.SelectedImageColor = null;
                    display.NotePage = 0;
                    display.NotePageCount = 0;
                    MouseUiHelper.SetDefaultImage(display, "lorc/open-book");
                    await MouseUiHelper.HandleGameScreen(settings, display, storage, screen, journalDisplay.Draw,
                        (s, dlg) => aiClient.SendAction(s, dlg == null ? null : dlg.AppendStreamChunk, dlg == null ? null : dlg.NewStreamingMessage, dlg == null ? null : dlg.TickSpinner, null),
                        title,
                        onScrollLeft:  () => journalDisplay.Flip(-1),
                        onScrollRight: () => journalDisplay.Flip(1),
                        onTab:         () => journalDisplay.SelectNext(1),
                        onShiftTab:    () => journalDisplay.SelectNext(-1),
                        onF6: () => journalDisplay.SetSection(JournalDisplay.Section.Quests),
                        onF7: () => journalDisplay.SetSection(JournalDisplay.Section.People),
                        onF8: () => journalDisplay.SetSection(JournalDisplay.Section.Locations),
                        onF9: () => journalDisplay.SetSection(JournalDisplay.Section.Bestiary),
                        onF10: () => journalDisplay.SetSection(JournalDisplay.Section.Notes),
                        onTextInput: journalDisplay.TryAddNote,
                        onDelete: journalDisplay.DeleteSelectedNote,
                        setActiveDialog: d => aiClient.ActiveDialog = d,
                        pollFactory: dlg => MouseUiHelper.MakeJournalPollAction(dlg, journalDisplay, title, display));
                    display.JournalShown = false;
                    break;

                // The "New level" window: make the choice and go back to the card (or to the tab clicked in the header).
                case Screen.LevelUp:
                {
                    display.JournalShown = false;
                    display.ActiveTabKey = "F4";
                    string? go = await levelUpDisplay.Run(title, heroDisplay);
                    state.CurrentScreen = Screen.Character;
                    if (go != null && screen.Commands.TryGetValue(go, out var goAction)) goAction();
                    break;
                }

                case Screen.Character:
                    heroDisplay.OfferLevelUp = true;
                    display.JournalShown = false;
                    display.ActiveTabKey = "F4";
                    display.SelectedInventoryIndex = -1;
                    display.SelectedImageLines = null;
                    display.SelectedImageColor = null;
                    display.PinnedInventoryIndex = -1;
                    display.NotePage = 0;
                    display.NotePageCount = 0;
                    MouseUiHelper.SetDefaultImage(display, settings.Hero?.Image, settings.Hero?.Color);
                    await MouseUiHelper.HandleGameScreen(settings, display, storage, screen, heroDisplay.DrawHeroCard,
                        (s, dlg) => aiClient.SendAction(s, dlg == null ? null : dlg.AppendStreamChunk, dlg == null ? null : dlg.NewStreamingMessage, dlg == null ? null : dlg.TickSpinner, null),
                        title,
                        heroDisplay:   heroDisplay,
                        onScrollLeft: () =>
                        {
                            switch (display.CharacterSubTab)
                            {
                                case CharacterSubTab.Inventory:
                                    display.SelectedInventoryIndex = display.InventoryPageFirstPrevItemIdx;
                                    display.InventoryScrollOffset = Math.Max(0, display.InventoryScrollOffset - 1);
                                    break;
                                case CharacterSubTab.Abilities:
                                    display.AbilityPageOffset = Math.Max(0, display.AbilityPageOffset - 1);
                                    break;
                                case CharacterSubTab.Spells:
                                    display.SpellsPageOffset = Math.Max(0, display.SpellsPageOffset - 1);
                                    break;
                                default:
                                    display.EffectsPageOffset = Math.Max(0, display.EffectsPageOffset - 1);
                                    break;
                            }
                        },
                        onScrollRight: () =>
                        {
                            switch (display.CharacterSubTab)
                            {
                                case CharacterSubTab.Inventory:
                                    display.SelectedInventoryIndex = display.InventoryPageFirstNextItemIdx;
                                    display.InventoryScrollOffset++;
                                    break;
                                case CharacterSubTab.Abilities:
                                    display.AbilityPageOffset++;
                                    break;
                                case CharacterSubTab.Spells:
                                    display.SpellsPageOffset++;
                                    break;
                                default:
                                    display.EffectsPageOffset++;
                                    break;
                            }
                        },
                        onTab: () =>
                        {
                            if (display.CharacterSubTab == CharacterSubTab.Spells)
                            {
                                var spells = settings.Hero == null ? [] : HeroDisplay.SortedSpells(settings.Hero);
                                if (spells.Count == 0) return;
                                if (display.SelectedSpellIndex < 0)
                                {
                                    display.SelectedSpellIndex = 0;
                                }
                                else if (display.SelectedSpellIndex == display.SpellsPageLastItemIdx
                                    && display.SpellsPageFirstNextItemIdx >= 0)
                                {
                                    display.SpellsPageOffset++;
                                    display.SelectedSpellIndex = display.SpellsPageFirstNextItemIdx;
                                }
                                else
                                {
                                    int next = display.SelectedSpellIndex + 1;
                                    if (next >= spells.Count) { display.SpellsPageOffset = 0; next = 0; }
                                    display.SelectedSpellIndex = next;
                                }
                                if (display.SelectedSpellIndex < spells.Count)
                                {
                                    var spell = spells[display.SelectedSpellIndex];
                                    display.PinnedSpellIndex = display.SelectedSpellIndex;
                                    MouseUiHelper.SetSelectedSpell(display, spell, 0);
                                }
                                return;
                            }
                            if (display.CharacterSubTab != CharacterSubTab.Inventory) return;
                            var items = settings.Hero?.Inventory ?? [];
                            if (items.Count == 0) return;
                            if (display.SelectedInventoryIndex < 0)
                            {
                                display.SelectedInventoryIndex = 0;
                            }
                            else if (display.SelectedInventoryIndex == display.InventoryPageLastItemIdx
                                && display.InventoryPageFirstNextItemIdx >= 0)
                            {
                                display.InventoryScrollOffset++;
                                display.SelectedInventoryIndex = display.InventoryPageFirstNextItemIdx;
                            }
                            else
                            {
                                int next = display.SelectedInventoryIndex + 1;
                                if (next >= items.Count) { display.InventoryScrollOffset = 0; next = 0; }
                                display.SelectedInventoryIndex = next;
                            }
                            if (display.SelectedInventoryIndex < items.Count)
                            {
                                var item = items[display.SelectedInventoryIndex];
                                display.PinnedInventoryIndex = display.SelectedInventoryIndex;
                                MouseUiHelper.SetSelectedInventoryItem(display, item, 0);
                            }
                        },
                        onShiftTab: () =>
                        {
                            if (display.CharacterSubTab == CharacterSubTab.Spells)
                            {
                                var spells = settings.Hero == null ? [] : HeroDisplay.SortedSpells(settings.Hero);
                                if (spells.Count == 0) return;
                                if (display.SelectedSpellIndex < 0)
                                {
                                    display.SpellsPageOffset = int.MaxValue;
                                    display.SelectedSpellIndex = spells.Count - 1;
                                }
                                else if (display.SelectedSpellIndex == display.SpellsPageFirstItemIdx
                                    && display.SpellsPageFirstPrevItemIdx >= 0)
                                {
                                    display.SpellsPageOffset--;
                                    display.SelectedSpellIndex = display.SpellsPageFirstPrevItemIdx;
                                }
                                else
                                {
                                    int prev = display.SelectedSpellIndex - 1;
                                    if (prev < 0) { display.SpellsPageOffset = int.MaxValue; prev = spells.Count - 1; }
                                    display.SelectedSpellIndex = prev;
                                }
                                if (display.SelectedSpellIndex < spells.Count && display.SelectedSpellIndex >= 0)
                                {
                                    var spell = spells[display.SelectedSpellIndex];
                                    display.PinnedSpellIndex = display.SelectedSpellIndex;
                                    MouseUiHelper.SetSelectedSpell(display, spell, 0);
                                }
                                return;
                            }
                            if (display.CharacterSubTab != CharacterSubTab.Inventory) return;
                            var items = settings.Hero?.Inventory ?? [];
                            if (items.Count == 0) return;
                            if (display.SelectedInventoryIndex < 0)
                            {
                                display.InventoryScrollOffset = int.MaxValue;
                                display.SelectedInventoryIndex = items.Count - 1;
                            }
                            else if (display.SelectedInventoryIndex == display.InventoryPageFirstItemIdx
                                && display.InventoryPageFirstPrevItemIdx >= 0)
                            {
                                display.InventoryScrollOffset--;
                                display.SelectedInventoryIndex = display.InventoryPageFirstPrevItemIdx;
                            }
                            else
                            {
                                int prev = display.SelectedInventoryIndex - 1;
                                if (prev < 0) { display.InventoryScrollOffset = int.MaxValue; prev = items.Count - 1; }
                                display.SelectedInventoryIndex = prev;
                            }
                            if (display.SelectedInventoryIndex < items.Count && display.SelectedInventoryIndex >= 0)
                            {
                                var item = items[display.SelectedInventoryIndex];
                                display.PinnedInventoryIndex = display.SelectedInventoryIndex;
                                MouseUiHelper.SetSelectedInventoryItem(display, item, 0);
                            }
                        },
                        onF6: () => { display.CharacterSubTab = CharacterSubTab.Inventory;  display.InventoryScrollOffset = 0; display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onF7: () => { display.CharacterSubTab = CharacterSubTab.Abilities; display.AbilityPageOffset = 0;  display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onF8: () => { display.CharacterSubTab = CharacterSubTab.Effects;   display.EffectsPageOffset = 0;  display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onF9: () => { if (!heroDisplay.HasSpells) return; display.CharacterSubTab = CharacterSubTab.Spells;    display.SpellsPageOffset = 0;   display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onInventorySelect: (idx, page) =>
                        {
                            var items = settings.Hero?.Inventory ?? [];
                            if (idx >= 0 && idx < items.Count)
                                MouseUiHelper.SetSelectedInventoryItem(display, items[idx], page);
                        },
                        onSpellSelect: (idx, page) =>
                        {
                            var spells = settings.Hero == null ? [] : HeroDisplay.SortedSpells(settings.Hero);
                            if (idx >= 0 && idx < spells.Count)
                                MouseUiHelper.SetSelectedSpell(display, spells[idx], page);
                        },
                        setActiveDialog: d => aiClient.ActiveDialog = d);
                    break;

                case Screen.Abilities:
                    display.JournalShown = false;
                    display.ActiveTabKey = "F5";
                    MouseUiHelper.SetDefaultImage(display, "delapouite/spell-book");
                    await MouseUiHelper.HandleGameScreen(settings, display, storage, screen, abilityDisplay.Draw,
                        (s, dlg) => aiClient.SendAction(s, dlg == null ? null : dlg.AppendStreamChunk, dlg == null ? null : dlg.NewStreamingMessage, dlg == null ? null : dlg.TickSpinner, null),
                        title,
                        onScrollUp:   () => display.AbilityScrollOffset = Math.Max(0, display.AbilityScrollOffset - 1),
                        onScrollDown: () => display.AbilityScrollOffset++,
                        setActiveDialog: d => aiClient.ActiveDialog = d);
                    break;

                // Step-by-step new game: 1 world → 2 hero (character sheet) → 3 editor → 4 adventure. What has been filled in lives in
                // state.NewGame (the step wizard and the sheet) — steps can be revisited back and forth (F1–F4); Esc exits to the
                // menu with a warning (Screen.NewGameExit).
                case Screen.NewGame:
                {
                    var ng = state.NewGame ??= new NewGameSession();
                    var wizard = ng.Wizard ??= new NewGameWizard(settings, display, aiClient);
                    wizard.Title = screen.Title;
                    wizard.CanGo = ng.CanGo;
                    int to = await wizard.PickWorld();
                    if (to == 0) { ng.ReturnTo = Screen.NewGame; state.CurrentScreen = Screen.NewGameExit; break; }
                    ng.WorldId = wizard.ChosenWorldId;
                    if (storage.NewGameData is { } chosen) chosen.WorldId = ng.WorldId;
                    state.CurrentScreen = NewGameSession.StepScreen(to);
                    break;
                }

                // The "Hero" step is the character sheet: a new hero or one from the hero library (the HERO field at the top).
                case Screen.NewGameHero:
                {
                    var ng = state.NewGame;
                    if (ng?.Wizard == null || ng.WorldId == null) { state.CurrentScreen = Screen.NewGame; break; }
                    Term.SetCursorPosition(0, ng.Wizard.StartTop);
                    // Came back and picked a world in another language — the sheet and hero start over (options and names are in the world's language).
                    if (ng.Form is { } old && old.Language != L.World) { ng.Form = null; ng.HeroReady = false; }
                    var form = ng.Form ??= new NewGameDisplay(settings, display, screen, aiClient);
                    form.CanGo = ng.CanGo;
                    int to = await form.Show();
                    if (to == 0) { ng.ReturnTo = Screen.NewGameHero; state.CurrentScreen = Screen.NewGameExit; break; }
                    if (to >= 3 && form.Data is { } data)
                    {
                        data.WorldId = ng.WorldId;
                        storage.NewGameData = data;
                        ng.HeroReady = true;
                        // A hero created by the AI goes straight into the hero library (not in UI tests).
                        if (testStatePath == null)
                        {
                            if (form.CreatedNow) { storage.SaveDraft(); HeroLibrary.SyncFromSave(Storage.DraftSavePath); }
                            HeroLibrary.SetDescription(data.Name, data.Description);   // the description goes with the hero card
                        }
                        form.CreatedNow = false;
                    }
                    state.CurrentScreen = NewGameSession.StepScreen(to);
                    break;
                }

                // The "Adventure" step (after the editor): wishes and the starting place; "START GAME" starts the game.
                case Screen.NewGameAdventure:
                {
                    var ng = state.NewGame;
                    if (ng?.Wizard == null || !ng.HeroReady) { state.CurrentScreen = Screen.Menu; break; }
                    ng.Wizard.Title = screen.Title;
                    ng.Wizard.CanGo = ng.CanGo;
                    var (to, adventure) = await ng.Wizard.PickAdventure(ng.WorldId ?? WorldLibrary.Current.Id, ng.Adventure);
                    ng.Adventure = adventure;
                    if (to == 0) { ng.ReturnTo = Screen.NewGameAdventure; state.CurrentScreen = Screen.NewGameExit; break; }
                    if (to != NewGameWizard.StartStep) { state.CurrentScreen = NewGameSession.StepScreen(to); break; }
                    if (storage.NewGameData is { } ngd)
                    {
                        ngd.WorldId = ng.WorldId;
                        ngd.SettingWish = adventure.Wish;
                        ngd.AdventureStyle = adventure.Style;
                        ngd.StartPlace = adventure.StartPlace;
                        ngd.StartX = adventure.StartTile?.x;
                        ngd.StartY = adventure.StartTile?.y;
                    }
                    state.NewGame = null;
                    screen.Commands["Start"]();
                    break;
                }

                // Exiting game creation to the menu comes with a warning; "stay" goes back to the same step.
                case Screen.NewGameExit:
                {
                    var ng = state.NewGame;
                    if (ng?.Wizard == null) { state.CurrentScreen = Screen.Menu; break; }
                    if (await ng.Wizard.ConfirmExit())
                    {
                        ng.Wizard.DiscardWorld();
                        state.NewGame = null;
                        state.CurrentScreen = Screen.Menu;
                    }
                    else state.CurrentScreen = ng.ReturnTo;
                    break;
                }

                case Screen.NewGameCharacter:
                    heroDisplay.OfferLevelUp = false;
                    display.ActiveTabKey = "F3";
                    display.SelectedInventoryIndex = -1;
                    display.SelectedImageLines = null;
                    display.SelectedImageColor = null;
                    display.PinnedInventoryIndex = -1;
                    display.NotePage = 0;
                    display.NotePageCount = 0;
                    MouseUiHelper.SetDefaultImage(display, settings.Hero?.Image, settings.Hero?.Color);
                    await MouseUiHelper.HandleGameScreen(settings, display, storage, screen, heroDisplay.DrawHeroCard,
                        (s, dlg) => aiClient.FixHeroForNewGame(s, dlg),
                        title,
                        heroDisplay:   heroDisplay,
                        onScrollLeft:  () =>
                        {
                            switch (display.CharacterSubTab)
                            {
                                case CharacterSubTab.Inventory:
                                    display.SelectedInventoryIndex = display.InventoryPageFirstPrevItemIdx;
                                    display.InventoryScrollOffset = Math.Max(0, display.InventoryScrollOffset - 1);
                                    break;
                                case CharacterSubTab.Abilities:
                                    display.AbilityPageOffset = Math.Max(0, display.AbilityPageOffset - 1);
                                    break;
                                case CharacterSubTab.Spells:
                                    display.SpellsPageOffset = Math.Max(0, display.SpellsPageOffset - 1);
                                    break;
                                default:
                                    display.EffectsPageOffset = Math.Max(0, display.EffectsPageOffset - 1);
                                    break;
                            }
                        },
                        onScrollRight: () =>
                        {
                            switch (display.CharacterSubTab)
                            {
                                case CharacterSubTab.Inventory:
                                    display.SelectedInventoryIndex = display.InventoryPageFirstNextItemIdx;
                                    display.InventoryScrollOffset++;
                                    break;
                                case CharacterSubTab.Abilities:
                                    display.AbilityPageOffset++;
                                    break;
                                case CharacterSubTab.Spells:
                                    display.SpellsPageOffset++;
                                    break;
                                default:
                                    display.EffectsPageOffset++;
                                    break;
                            }
                        },
                        onTab: () =>
                        {
                            if (display.CharacterSubTab == CharacterSubTab.Spells)
                            {
                                var spells = settings.Hero == null ? [] : HeroDisplay.SortedSpells(settings.Hero);
                                if (spells.Count == 0) return;
                                if (display.SelectedSpellIndex < 0)
                                {
                                    display.SelectedSpellIndex = 0;
                                }
                                else if (display.SelectedSpellIndex == display.SpellsPageLastItemIdx
                                    && display.SpellsPageFirstNextItemIdx >= 0)
                                {
                                    display.SpellsPageOffset++;
                                    display.SelectedSpellIndex = display.SpellsPageFirstNextItemIdx;
                                }
                                else
                                {
                                    int next = display.SelectedSpellIndex + 1;
                                    if (next >= spells.Count) { display.SpellsPageOffset = 0; next = 0; }
                                    display.SelectedSpellIndex = next;
                                }
                                if (display.SelectedSpellIndex < spells.Count)
                                {
                                    var spell = spells[display.SelectedSpellIndex];
                                    display.PinnedSpellIndex = display.SelectedSpellIndex;
                                    MouseUiHelper.SetSelectedSpell(display, spell, 0);
                                }
                                return;
                            }
                            if (display.CharacterSubTab != CharacterSubTab.Inventory) return;
                            var items = settings.Hero?.Inventory ?? [];
                            if (items.Count == 0) return;
                            if (display.SelectedInventoryIndex < 0)
                            {
                                display.SelectedInventoryIndex = 0;
                            }
                            else if (display.SelectedInventoryIndex == display.InventoryPageLastItemIdx
                                && display.InventoryPageFirstNextItemIdx >= 0)
                            {
                                display.InventoryScrollOffset++;
                                display.SelectedInventoryIndex = display.InventoryPageFirstNextItemIdx;
                            }
                            else
                            {
                                int next = display.SelectedInventoryIndex + 1;
                                if (next >= items.Count) { display.InventoryScrollOffset = 0; next = 0; }
                                display.SelectedInventoryIndex = next;
                            }
                            if (display.SelectedInventoryIndex < items.Count)
                            {
                                var item = items[display.SelectedInventoryIndex];
                                display.PinnedInventoryIndex = display.SelectedInventoryIndex;
                                MouseUiHelper.SetSelectedInventoryItem(display, item, 0);
                            }
                        },
                        onShiftTab: () =>
                        {
                            if (display.CharacterSubTab == CharacterSubTab.Spells)
                            {
                                var spells = settings.Hero == null ? [] : HeroDisplay.SortedSpells(settings.Hero);
                                if (spells.Count == 0) return;
                                if (display.SelectedSpellIndex < 0)
                                {
                                    display.SpellsPageOffset = int.MaxValue;
                                    display.SelectedSpellIndex = spells.Count - 1;
                                }
                                else if (display.SelectedSpellIndex == display.SpellsPageFirstItemIdx
                                    && display.SpellsPageFirstPrevItemIdx >= 0)
                                {
                                    display.SpellsPageOffset--;
                                    display.SelectedSpellIndex = display.SpellsPageFirstPrevItemIdx;
                                }
                                else
                                {
                                    int prev = display.SelectedSpellIndex - 1;
                                    if (prev < 0) { display.SpellsPageOffset = int.MaxValue; prev = spells.Count - 1; }
                                    display.SelectedSpellIndex = prev;
                                }
                                if (display.SelectedSpellIndex < spells.Count && display.SelectedSpellIndex >= 0)
                                {
                                    var spell = spells[display.SelectedSpellIndex];
                                    display.PinnedSpellIndex = display.SelectedSpellIndex;
                                    MouseUiHelper.SetSelectedSpell(display, spell, 0);
                                }
                                return;
                            }
                            if (display.CharacterSubTab != CharacterSubTab.Inventory) return;
                            var items = settings.Hero?.Inventory ?? [];
                            if (items.Count == 0) return;
                            if (display.SelectedInventoryIndex < 0)
                            {
                                display.InventoryScrollOffset = int.MaxValue;
                                display.SelectedInventoryIndex = items.Count - 1;
                            }
                            else if (display.SelectedInventoryIndex == display.InventoryPageFirstItemIdx
                                && display.InventoryPageFirstPrevItemIdx >= 0)
                            {
                                display.InventoryScrollOffset--;
                                display.SelectedInventoryIndex = display.InventoryPageFirstPrevItemIdx;
                            }
                            else
                            {
                                int prev = display.SelectedInventoryIndex - 1;
                                if (prev < 0) { display.InventoryScrollOffset = int.MaxValue; prev = items.Count - 1; }
                                display.SelectedInventoryIndex = prev;
                            }
                            if (display.SelectedInventoryIndex < items.Count && display.SelectedInventoryIndex >= 0)
                            {
                                var item = items[display.SelectedInventoryIndex];
                                display.PinnedInventoryIndex = display.SelectedInventoryIndex;
                                MouseUiHelper.SetSelectedInventoryItem(display, item, 0);
                            }
                        },
                        onF6: () => { display.CharacterSubTab = CharacterSubTab.Inventory;  display.InventoryScrollOffset = 0; display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onF7: () => { display.CharacterSubTab = CharacterSubTab.Abilities; display.AbilityPageOffset = 0;  display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onF8: () => { display.CharacterSubTab = CharacterSubTab.Effects;   display.EffectsPageOffset = 0;  display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onF9: () => { if (!heroDisplay.HasSpells) return; display.CharacterSubTab = CharacterSubTab.Spells;    display.SpellsPageOffset = 0;   display.SelectedInventoryIndex = -1; display.SelectedSpellIndex = -1; display.PinnedSpellIndex = -1; display.SelectedImageLines = null; display.SelectedImageColor = null; display.PinnedInventoryIndex = -1; display.NotePage = 0; display.NotePageCount = 0; },
                        onInventorySelect: (idx, page) =>
                        {
                            var items = settings.Hero?.Inventory ?? [];
                            if (idx >= 0 && idx < items.Count)
                                MouseUiHelper.SetSelectedInventoryItem(display, items[idx], page);
                        },
                        onSpellSelect: (idx, page) =>
                        {
                            var spells = settings.Hero == null ? [] : HeroDisplay.SortedSpells(settings.Hero);
                            if (idx >= 0 && idx < spells.Count)
                                MouseUiHelper.SetSelectedSpell(display, spells[idx], page);
                        },
                        setActiveDialog: d => aiClient.ActiveDialog = d);
                    break;

                case Screen.NewGameAbilities:
                    display.ActiveTabKey = "F2";
                    await MouseUiHelper.HandleGameScreen(settings, display, storage, screen, abilityDisplay.Draw,
                        (s, dlg) => aiClient.FixHeroForNewGame(s, dlg),
                        title,
                        onScrollUp:   () => display.AbilityScrollOffset = Math.Max(0, display.AbilityScrollOffset - 1),
                        onScrollDown: () => display.AbilityScrollOffset++,
                        setActiveDialog: d => aiClient.ActiveDialog = d);
                    break;
            }
        }
    }
}

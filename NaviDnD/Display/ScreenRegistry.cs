namespace NaviDnD;

internal static class ScreenRegistry
{
    private static readonly (Screen screen, string key)[] GameTabs =
        [(Screen.Map, "F1"), (Screen.World, "F2"), (Screen.Journal, "F3"), (Screen.Character, "F4"), (Screen.LevelUp, "F4"), (Screen.Abilities, "F5")];

    // Game screen title: the active tab is "  → NAME ←", the others "[Fn]NAME  ", all the same length,
    // so the names don't shift when switching (MouseUiHelper.ComputeTitleTabs). Something new in the journal:
    // «JOURNAL•» (the dot takes the place of a space, same length). No tactical map (hero is traveling, scene
    // is text only): «{F1}MAP» is drawn grey and can't be clicked (MouseUiHelper.WriteColoredTitle/ComputeTitleTabs).
    internal static string GameTitleText(string activeKey, bool journalUnread, bool mapDisabled = false)
    {
        (string key, string name)[] tabs =
            [("F1", L.T("MAP")), ("F2", L.T("WORLD")), ("F3", L.T("JOURNAL")), ("F4", L.T("CHARACTER")), ("F5", L.T("COMPANION"))];
        return string.Concat(tabs.Select(t => "   " + (t.key == activeKey ? $"  → {t.name} ←"
                   : t.key == "F1" && mapDisabled ? $"{{{t.key}}}{t.name}  "
                   : t.key == "F3" && journalUnread ? $"[{t.key}]{t.name}• " : $"[{t.key}]{t.name}  ")))
               + "   " + L.T("[Esc]MENU") + "   ";
    }

    // New game title: steps look like game tabs («  → EDITOR ←», «[F1]WORLD  »; a step not yet available is
    // «{F4}ADVENTURE», dark and unclickable) joined by «›» to show stage order; on the right, «[Esc]MENU».
    internal static string NewGameTitle(int step, Func<int, bool>? canGo = null)
    {
        string[] steps = [L.T("WORLD"), L.T("HERO"), L.T("EDITOR"), L.T("ADVENTURE")];
        return L.T(" NEW GAME") + string.Concat(steps.Select((s, i) => (i == 0 ? "   " : "  ›  ") + (i + 1 == step ? $"  → {s} ←"
                   : canGo?.Invoke(i + 1) ?? i == 0 ? $"[F{i + 1}]{s}  " : $"{{F{i + 1}}}{s}  "))) + "   " + L.T("[Esc]MENU") + "   ";
    }

    // New game step titles, depending on which steps are already available.
    internal static void RefreshNewGameTitles(Dictionary<Screen, ScreenConfig> screens, NewGameSession ng)
    {
        foreach (var s in new[] { Screen.NewGame, Screen.NewGameHero, Screen.NewGameCharacter, Screen.NewGameAbilities, Screen.NewGameAdventure })
            screens[s].Title = NewGameTitle(NewGameSession.StepOf(s), ng.CanGo);
        screens[Screen.NewGameExit].Title = NewGameTitle(NewGameSession.StepOf(ng.ReturnTo), ng.CanGo);
    }

    // Refresh game screen titles (the journal's new-entry mark, a grey «Map» without a tactical map).
    internal static void RefreshGameTitles(Dictionary<Screen, ScreenConfig> screens, bool journalUnread, bool mapDisabled = false)
    {
        foreach (var (screen, key) in GameTabs)
            if (screens.TryGetValue(screen, out var cfg)) cfg.Title = GameTitleText(key, journalUnread, mapDisabled);
    }

    internal static Dictionary<Screen, ScreenConfig> GetScreens(
        GameState state, Storage storage, Func<bool> hasSave, Action loadCurrentState, Func<bool> hasLocation)
    {
        void GoToMenu()
        {
            state.LastGameScreen = state.CurrentScreen;
            state.CurrentScreen = Screen.Menu;
        }

        void StartGame()
        {
            state.CurrentScreen = Screen.Map;
            state.PendingStartNewGame = true;
        }

        ScreenConfig GameScreen(string title) => new()
        {
            Title = title,
            Commands =
            {
                // Map (local area) only when it exists; otherwise the tab is grey and F1 doesn't open it.
                ["F1"] = () => { if (hasLocation()) state.CurrentScreen = Screen.Map; },
                ["F2"] = () => state.CurrentScreen = Screen.World,
                ["F3"] = () => state.CurrentScreen = Screen.Journal,
                ["F4"] = () => state.CurrentScreen = Screen.Character,
                ["F5"] = () => state.CurrentScreen = Screen.Abilities,
                ["Esc"] = GoToMenu,
            }
        };

        // Game screen title: the active tab is "  → NAME ←", the others "[Fn]NAME  ", all the same
        // length, so the names don't shift when switching (MouseUiHelper.ComputeTitleTabs).
        string GameTitle(string activeKey) => GameTitleText(activeKey, journalUnread: false);

        // «Character»: F10 (or the button on the sheet) opens the «Level Up» window when there's enough XP and no combat.
        ScreenConfig CharacterScreen()
        {
            var cfg = GameScreen(GameTitle("F4"));
            cfg.Commands["F10"] = () => { if (LevelUp.Available(storage.WorldState)) state.CurrentScreen = Screen.LevelUp; };
            return cfg;
        }

        // New game step (F1–F4), if already available; Esc exits to the menu with a warning.
        void GoStep(int step)
        {
            if (state.NewGame is { } ng && ng.CanGo(step)) state.CurrentScreen = NewGameSession.StepScreen(step);
        }

        void ExitNewGame()
        {
            if (state.NewGame is { } ng) ng.ReturnTo = state.CurrentScreen;
            state.CurrentScreen = state.NewGame != null ? Screen.NewGameExit : Screen.Menu;
        }

        ScreenConfig EditorScreen(string title) => new()
        {
            Title = title,
            Commands =
            {
                ["F1"] = () => GoStep(1),
                ["F2"] = () => GoStep(2),
                ["F4"] = () => GoStep(4),   // «Adventure» step, then start
                ["Esc"] = ExitNewGame,
            }
        };

        return new Dictionary<Screen, ScreenConfig>
        {
            [Screen.Map]      = GameScreen(GameTitle("F1")),
            [Screen.World]    = GameScreen(GameTitle("F2")),
            [Screen.Journal]  = GameScreen(GameTitle("F3")),
            [Screen.Character]= CharacterScreen(),
            [Screen.LevelUp]  = GameScreen(GameTitle("F4")),
            [Screen.Abilities]= GameScreen(GameTitle("F5")), // «Companion» tab (a stub for now)

            [Screen.Menu] = new ScreenConfig
            {
                Title = L.T(" MENU   [F1]CONTINUE     [F2]NEW GAME     [Esc]QUIT"),
                Commands =
                {
                    ["F1"] = () =>
                    {
                        if (hasSave())
                        {
                            loadCurrentState();
                            state.CurrentScreen = Screen.Map;
                        }
                    },
                    ["F2"] = () => state.CurrentScreen = Screen.NewGame,
                    ["Esc"] = () => ConsoleSetup.CloseConsoleAndExit()
                }
            },

            // The «Adventure» step of the new game wizard (after the hero editor): wishes and the starting place.
            // New game steps: each has its own screen; transitions are handled by the screens (Program) and editor commands.
            [Screen.NewGameAdventure] = new ScreenConfig { Title = NewGameTitle(4), Commands = { ["Start"] = StartGame } },
            [Screen.NewGame] = new ScreenConfig { Title = NewGameTitle(1) },
            [Screen.NewGameHero] = new ScreenConfig { Title = NewGameTitle(2) },
            [Screen.NewGameExit] = new ScreenConfig { Title = NewGameTitle(1) },

            // Editor: the «EDITOR» step of a new game; the title shows only the steps (no character/companion tabs).
            [Screen.NewGameCharacter] = EditorScreen(NewGameTitle(3)),
            [Screen.NewGameAbilities] = EditorScreen(NewGameTitle(3)),
        };
    }
}

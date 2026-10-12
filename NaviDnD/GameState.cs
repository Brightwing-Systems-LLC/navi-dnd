namespace NaviDnD;

public enum Screen
{
    Map,
    Journal,
    Character,
    LevelUp,      // the "New level" window over the "Character" tab
    Abilities,
    World,
    Menu,
    NewGame,
    NewGameCharacter,
    NewGameAbilities,
    NewGameAdventure,
    NewGameHero,
    NewGameExit
}

public class GameState
{
    public Screen CurrentScreen { get; set; } = Screen.Menu;

    public Screen LastGameScreen { get; set; } = Screen.Menu;

    public bool PendingStartNewGame { get; set; }

    // Slot (Storage.SlotPath) for the game being created — becomes active when the world starts.
    public int? NewGameSlot { get; set; }

    // The game being created (steps 1–4) — until it starts or the player exits to the menu.
    public NewGameSession? NewGame { get; set; }

}

// Step-by-step new game creation (1 world → 2 hero → 3 editor → 4 adventure): what has been filled in lives until the game starts
// or the player exits to the menu — steps can be revisited back and forth (F1–F4, clicking a step in the header).
public class NewGameSession
{
    public Display.NewGameWizard? Wizard { get; set; }
    public Display.NewGameDisplay? Form { get; set; }
    public string? WorldId { get; set; }            // step 1 done
    public bool HeroReady { get; set; }             // step 2 done — the hero is created
    public Display.NewGameWizard.Adventure? Adventure { get; set; }
    public Screen ReturnTo { get; set; } = Screen.NewGame;   // exit to menu cancelled — return here

    public bool CanGo(int step) => step <= 1 || (step == 2 ? WorldId != null : HeroReady);

    public static Screen StepScreen(int step) => step switch
    {
        1 => Screen.NewGame, 2 => Screen.NewGameHero, 3 => Screen.NewGameCharacter, _ => Screen.NewGameAdventure,
    };

    public static int StepOf(Screen screen) => screen switch
    {
        Screen.NewGame => 1, Screen.NewGameHero => 2, Screen.NewGameAdventure => 4, _ => 3,
    };
}

public class ScreenConfig
{
    public string Title { get; set; }
    public Dictionary<string, Action> Commands { get; set; } = new();
}

public class NewGameData
{
    public string Name { get; set; }
    public string Symbol { get; set; }
    public string? Race { get; set; }
    public string? Class { get; set; }
    public string? Level { get; set; }
    public string? Alignment { get; set; }
    public string? Background { get; set; }
    public string Description { get; set; }
    public string Setting { get; set; }
    public string SettingWish { get; set; }
    // Adventure parameters as a string ("Genre: …; Difficulty: …; Pace: …") — for StartNewGame and narrative.style.
    public string? AdventureStyle { get; set; }
    // Chosen by the player on the character sheet — carried over to the hero as is (the AI does not re-pick them).
    public List<int>? Color { get; set; }
    public string? Image { get; set; }
    // The game's world: a library world id (continue its chronicle) or null for a new world of size WorldSize.
    public string? WorldId { get; set; }
    public string WorldSize { get; set; } = "small";
    // "Adventure" step: the starting place the player picked on the world map (a chronicle place or a cell) — optional.
    public string? StartPlace { get; set; }
    public int? StartX { get; set; }
    public int? StartY { get; set; }
}

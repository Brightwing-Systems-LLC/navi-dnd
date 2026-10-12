# D&D in the console with an AI Dungeon Master: why you can't trust the model with the dice

*Hubs: C#, .NET, Game development, Artificial intelligence, Open source*

> **TL;DR.** I'm building NaviDnD, a tabletop role-playing game where the Dungeon Master is played by Claude or OpenAI Codex. It's a minimalist console application: no 3D graphics and no game engine, just text, pseudographics and Braille-font pictures in a terminal window. The core idea: the model tells the story, while the world state, geometry, rolls and experience belong to the code. About 45 thousand lines of C#/.NET 9, plus a custom MCP server with game tools. The project is free and the source is open. I'm looking for people who'd like to develop it together. Links at the end.

![A combat turn: the heroine strikes a zombie with two swords, the player rolls the dice, the DM narrates the result](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/g4_combat_turn.gif)
*A real turn recorded in the game (the waits for the model are cut out, the text is sped up). The player writes an action, the DM asks for a roll, the die is rolled on screen, the engine deducts hit points.*

## How it all started

If you've ever run D&D, you know the main problem: there's no DM. Or there is one, but once a month. The logical thought is "let a neural network be the DM". And for the first ten minutes of chatting with a model it really does look magical.

Then it begins:

- the goblin had 7 hit points, you dealt 9, and yet it "rises with the last of its strength";
- you entered the room from the west, and three turns later the door "behind you" is to the north;
- the model "rolls the die" for you, and somehow it always comes up with a dramatic number;
- an hour into the game the context has bloated and the DM forgets what's in your backpack.

A chat is a bad engine. A model is great at inventing, describing and playing characters, but it isn't obliged to, and can't, keep exact state and count honestly. Hence the principle the whole project is built on:

**The model is the storyteller and the referee. State, geometry, dice and progression belong to the code.**

## Why the console

A tabletop RPG is first and foremost imagination. Players at the table make do with a character sheet, a grid map and the DM's voice. NaviDnD works the same way: one console window, a dark background, a monospaced font. The map is drawn with characters, monsters are marked with a short tag like `ZM1`, and all the atmosphere lives in the DM's text.

![The start of an adventure: the heroine at a barrow in the marshes, fog of war, the DM's first line](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/g3_intro.gif)

Minimalism has practical advantages too:

- **Nothing distracts from the story.** The whole screen is given over to the map, the log and the character sheet.
- **A low barrier for developers.** No Unity, shaders or gigabytes of assets, just plain C#. You don't need to learn a game engine to add a mechanic.
- **Modest hardware requirements.** The game runs on a weak Windows laptop, and the heavy lifting is done by the model on the provider's side.
- **Everything is testable.** UI tests read the console buffer as text, so any screen can be checked in an automated test.

## How it works

```
 Player ──► Console UI (C#) ──► GameAiClient ──► claude / codex CLI
              ▲                                       │
              │      JSON patches + history           │  tool calls
              │                                       ▼
          Storage ◄──────────────────────── NaviDnD.McpServer
       (WorldState)        roll_dice, select_target, calculate_movement,
                           plan_location, find_monster, get_world_state…
```

The solution consists of several projects:

| Project | What it does |
| --- | --- |
| `NaviDnD` | game loop, console UI, storage, AI clients |
| `NaviDnD.Data` | world state models |
| `NaviDnD.MapGen` | geometry of tactical maps and the world map |
| `NaviDnD.McpServer` | game tools for the model (MCP) |
| `NaviDnD.Tests` / `UiTests` / `PromptTests` | logic, UI through a real process, scenarios with real models |

### The model doesn't write the state, it sends patches

On every turn the DM returns JSON: a list of `history` entries (what the player will see and hear) and patches to the world state. The patches are applied by `Storage.ApplyUpdateWorldState`, and each field has its own rules.

Collections (creatures, rooms, hero resources) use the `PatchByIndex` scheme: `id` is the index in the current list, the fields passed are updated selectively, and `deleted: true` removes the element. The model doesn't need to resend the whole world, and getting one field wrong is much safer than "redrawing" the entire map.

Some fields the model can't change at all. For example, `hero.level` and `hero.xp` in the DM's response are ignored. The DM can only *award* experience: pass `xpAward` with a list of keys of defeated monsters. How much experience they're worth is calculated by the engine from the rules table, and the engine also decides when the hero can level up. Leveling up itself is a separate screen: hit points, proficiency bonus and ability score improvements are computed by the code, and the model only proposes the things where the rules give a choice (spells, features).

### The player rolls the dice, not the model

When the rules call for a d20 roll, the model calls the MCP tool `roll_dice`. The game shows a die animation, the DC (unless it's hidden) and the modifiers, and the model gets back `{ roll, total }`.

![A roll with disadvantage: two d20s, DC 8, Dexterity modifiers and proficiency bonus](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/07_dice_disadvantage.png)
*The heroine fires her bow point-blank, and the DM applies disadvantage on its own, by the rules ("[Disadvantage: enemy within 5 ft]"). The player sees both dice, the DC and the modifiers.*

Honesty works both ways. In the same session the second shot came up a natural one, and the DM honestly described the arrow vanishing into the bog. You can't ask the model to "tweak" the result: the number came from the player.

It quickly turned out that models love to roll a die "for atmosphere". So the tool validates requests and rejects meaningless ones: for example, a roll with no modifiers and no DC is almost certainly not a real 5e check (most often it's an attempt to "roll" for activating Rage, which requires no roll). The model gets an error with an explanation and goes on with the story.

`select_target` (the player picks the target or a point on the map themselves) and `calculate_movement` (paths and distances in combat are computed by the code, not eyeballed by the model) work on the same principle.

### Maps: the model plans, the code builds

A location is created in two steps. The DM calls `plan_location` and describes the intent: the type (dungeon, cave, open terrain), rooms, mood. The geometry is built by `NaviDnD.MapGen`, and further chunks of the map are filled in as they are explored by a separate `PopulateChunk` prompt. Coordinates, fog of war, doors, visibility and passability are all on the code's side.

An important side effect: **ordinary walking around the map doesn't call the model at all**. The game turns to the neural network only when something significant happens: a trigger fires, the player does something or speaks. That saves both time and subscription limits.

![Hovering the mouse over the map: the path, remaining movement, highlighting in the legend](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/g2_map_hover.gif)
*The path under the cursor and the remaining movement are computed by the code ("30 ft · 0 left"), without a single request to the model.*

The world map is procedural too. The code generates continents, rivers, mountains and kingdoms, and the model, at the player's request, writes a description of the world and then runs the story within that geography.

![World map generation: several variants in a row](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/g1_worldgen.gif)

### Why the CLI and not the API

The game doesn't ask for an API key. It finds the user's installed Claude Code or Codex CLI (in PATH, in npm, in the VS Code, Cursor and Windsurf extensions), launches it as a process and connects its own MCP server to it. The player uses the subscription they already have and doesn't pay for tokens separately.

Both providers share a common JSON protocol and one set of MCP tools. The provider is chosen in the settings; switching is handled by `SwitchableAiProvider`.

### A minimalist console that looks like a game

The entire interface is a console application built on Spectre.Console. The map, tabs, character sheet, journal, bestiary. Monster and item icons and portraits are SVGs that are converted to Braille in the background (`SvgToBrailleConverter`).

![Creating the heroine: the questionnaire, a Braille portrait of an archer and a description written by the model](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/03_hero.png)
*The player fills in the questionnaire, the model writes the description from a short prompt, and the portrait is an SVG drawn with Braille dots.*

![The character sheet, assembled from the questionnaire](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/en/character_sheet.png)

To keep the console from flickering I had to write my own mini-renderer:

- `AnimationLoop` runs parallel animations off a monotonic clock: text typing, a spinner, the die, the hero moving along a path;
- `ConsoleRegionBuffer` caches lines and redraws only the cells that changed, for example on mouse hover;
- `AnsiColorRuns` collapses repeated ANSI color commands;
- dragging the map with the mouse is handled in the shared input polling loop, without nested blocking loops.

### Offline voice-over

You can listen to the DM's lines. The model marks up the text with SSML, the engine strips out the mechanics (`[Attack: 17 vs AC 15]`) and hands the text to a local Silero model. The build contains a portable Python, the CPU version of PyTorch and the model, so nothing needs to be downloaded and no internet is required. Synthesis runs in a separate process with a queue and doesn't block the UI.

### Updates via patches

The full build with Python and Silero weighs about 500 MB, and pushing it for every fix would be torture for players. So releases since 1.1.5 arrive as diff patches: a script compares the file manifest with the previous version, packs only what changed, and the update helper verifies SHA256 and rolls back the changes on any error. Saves and settings aren't touched, and when the data format changes, migrations run with a backup.

## How to test something that answers differently every time

This is perhaps the most interesting engineering part. There are three layers of tests:

1. **`NaviDnD.Tests`**: more than 200 logic tests without a neural network: applying patches, geometry, migrations, level rules.
2. **`NaviDnD.UiTests`**: launch a real game process and drive the console UI.
3. **`NaviDnD.PromptTests`**: scenarios with real model responses. What's checked isn't that the JSON is valid but the *consequences*: after an attack the goblin's hit points really went down, after a door is opened it's open on the map.

A rule we hold to in the project: check the consequences of a response in the state, not just the validity of the response.

## An engine, not just D&D

The D&D 5e rules live in prompts (`Prompts/Dnd5e/{Action}/systemPrompt.md`) and reference data (`GameData`: monsters, spells, equipment, the level table). The engine doesn't depend on the rule system, so in theory another system could be plugged in. This is one of the areas where people who know other tabletop RPGs well are especially needed.

## What's already there

- hero and world creation, a world map with travel, time and random encounters;
- grid-based tactical combat: initiative, reactions, target selection, per-round effects;
- dungeons, caves, open terrain, fog of war, darkvision;
- a quest journal, character sheet, bestiary, leveling up;
- Russian and English (the interface language and the world language are chosen separately);
- offline voice-over, automatic patch updates;
- current version 2.2.3, an installer for Windows x64.

![The climax of the fight: a crit with the second blade, the engine deducts hit points](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/08_combat_crit.png)
*The mechanics in square brackets are what the engine calculated and applied: "[CRIT 25 vs AC8, damage 9, ZM1 6→0]".*

![The world map with regions, cities and the hero's position](https://gitlab.com/navitalevich/navi-dnd/-/raw/main/docs/media/en/world_map.png)

## Being honest about the problems

- **Latency.** A turn with tool calls can take tens of seconds. I'd like to show entries as they're generated and cut unnecessary calls.
- **The main-turn prompt weighs about 59 KB.** Every rule in it was hard-won, but it needs to be shortened and structured.
- **The model confuses identical creatures** ("which of the three skeletons?") and sometimes its description diverges from the mechanics.
- **Windows only.** The console layer is tied to WinAPI.

## Who I'm looking for

Right now I'm building the project alone (plus Claude and Codex as coding partners). I'd be glad to have:

- **C#/.NET developers**: the engine, the map, combat, console rendering performance;
- **people with LLM and prompt-engineering experience**: shortening prompts, latency, tool-calling reliability, eval scenarios;
- **D&D DMs and players**: playtests, bug reports, rules checking; especially those who run other systems;
- **people who know cross-platform terminals**: a port to Linux and macOS;
- **artists and sound designers**: icons, music, ambience.

Open tasks are collected in [docs/roadmap.md](https://gitlab.com/navitalevich/navi-dnd/-/blob/main/docs/roadmap.md), the architecture is described in [docs/architecture.md](https://gitlab.com/navitalevich/navi-dnd/-/blob/main/docs/architecture.md), and building from source takes a couple of commands ([docs/development.md](https://gitlab.com/navitalevich/navi-dnd/-/blob/main/docs/development.md)).

## Links

- Repository and releases: https://gitlab.com/navitalevich/navi-dnd
- Download the latest version: https://gitlab.com/navitalevich/navi-dnd/-/releases/permalink/latest
- Telegram @navitalevich

License: free for non-commercial use with attribution preserved; selling only with the author's permission.

If you have Claude Code or Codex, download it, play one adventure and write in the comments where the DM broke. That's the best contribution to start with.

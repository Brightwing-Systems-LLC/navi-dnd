# NaviDnD architecture

A Windows role-playing game with an AI Dungeon Master. The D&D 5e rules are defined by prompts; the engine is responsible for state, geometry, rendering and interaction with the player.

## UI animations

`AnimationLoop` advances independent animations on a monotonic clock. Each frame has its own duration: the die keeps its pause after every frame (`catchUp: false`) and starts once the sound has started; text uses `catchUp: false` with a 40 ms pause after each character, so that after a stall it does not print the backlog of characters in one jerk. `Start` registers parallel animations, `PlayAsync` awaits a sequence, `DelayAsync` advances animations and polls input. Frame handlers run synchronously from `Tick`: they must not start background console writes or block the handler waiting for input. A registration must be released when its screen closes.

The dialog uses a shared loop for text, the spinner, the die and movement. The spinner redraws only the bottom area; the dialog skips unchanged lines. Dragging the map and the window is handled while polling input, without nested blocking loops. Mouse moves are coalesced into one frame; while dragging only the map field is redrawn, the legend after release. `AnsiColorRuns` trims repeated color commands in the map buffer. The order in which game patches are applied is preserved.

`ConsoleRegionBuffer` keeps separate line caches for text and for the picture; hovering redraws only the affected cells, the legend and the picture. `SvgToBrailleConverter.PrepareAsync` prepares images in the background; applying and drawing them stays in the UI loop. Cell reachability for the path under the cursor is cached until the map or hero state changes.

## Projects

| Project | Purpose |
| --- | --- |
| NaviDnD | Game loop, UI, storage, AI clients |
| NaviDnD.Data | Shared state and display models |
| NaviDnD.MapGen | Tactical map and world geometry |
| NaviDnD.McpServer | Game tools for both CLI providers |
| NaviDnD.Tests | Logic tests without a real model |
| NaviDnD.UiTests | Console UI through the real game process |
| NaviDnD.PromptTests | Scenarios with real model responses |

## Game flow

`Program` loads settings, creates the storage directories and assembles `Storage`, `GameAiClient` and the screens. `MapScreenLoop` runs the game; `MovementHandler` performs movement and detects triggers. Ordinary walking does not call the model.

`GameAiClient` picks the action prompt and the context from `AiContextBuilder`. `SwitchableAiProvider` picks Claude or Codex based on the current settings. `AiActionTools` defines the action's tools and the state path for MCP. Hero creation and the opening use a draft; regular turns use the active save.

The DM returns JSON with patches and `history`. `SequentialHistoryPlayer` applies the root patch, then shows the entries one by one, animates `movePath` and applies nested `patch`es. Rolls, reactions and target selection go through MCP and file-based requests to the UI; the old `triggerReaction` marker is not part of the current protocol.

`Speech` voices new history entries and MCP lines through a local Silero in a separate bounded queue. Synthesis and audio do not block the UI; Enter, Esc and F11 cancel the current speech and the queue. The enable, volume, voice and Python path settings are stored only on the player's machine. The release build ships a portable Python, CPU PyTorch and the model in `Speech/silero/`: nothing is downloaded when speech is enabled. The bundle is prepared by `prepare-silero-bundle.ps1`; personal files from Storage are not copied. When running from source without the bundle, installing into `Storage/Speech/silero/` via `setup-silero.ps1` is still supported. Speech is off by default. `SileroSpeech` drives a Python process over JSON lines: `silero_worker.py` loads the model once and produces a WAV for each line. Cancelling during synthesis kills the process; the next line will start it again.

## State and patches

`history[].text` may contain SSML `<speak>`. `Speech.DisplayText` produces plain on-screen text, keeping mechanics in `[...]`; `Speech.ValidateSsml` keeps the allowed markup and excludes mechanics from speech. `DialogMessage.SpeechText` is a local derived copy for speech, not a separate field of the model's response. The conversion happens in `Storage.ApplyUpdateWorldState`, `SequentialHistoryPlayer` and the MCP history flow. `AiContextBuilder` gives the model only the cleaned history. `Speech.Initialize` queues a silent Silero warm-up in advance without blocking the menu.

`WorldState` holds the hero, the map, history, time, combat, the story and the link to the world. `CellEntity` defines a map object, `LivingEntity` adds hit points and effects, `Hero` adds ability scores, resources, actions and equipment. JSON uses camelCase; reading is case-insensitive.

`Storage.ApplyUpdateWorldState` applies changes. For collections with `PatchByIndex`:

- `id` is the index in the current list; without `id` the element is appended.
- An existing index updates only the fields sent; a new index adds an element.
- `deleted: true` removes the element if the collection allows removal; removals are applied from the end of the list.
- The mechanism is used for the hero's stats and resources, rooms, creatures and objects. The outdated rule "replace these lists wholesale" is wrong.
- Images are updated by `key`. Other fields are handled by their own `Apply*`; the exact contract lives in `Storage.cs` and the prompts.

Tactical map coordinates are `[col, row]`, starting at 1; entity symbols are three characters. For index-based patches the DM gets the data through `get_world_state`; keys and filters are defined by `WorldStateTools` and `AiContextBuilder.Serialize`.

## Experience and levels

`hero.level` and `hero.xp` are owned by the engine: they are not applied from the DM's response, only from a save (`Storage.ApplySavedState` — loading, MCP, a library hero). The DM awards experience with the `hero.xpAward` patch (`monsters` — the `monsterKey`s of defeated monsters, XP by challenge rating; `xp` — for everything else); the engine writes the total and "level up available" into the history after the response. The rules table is `GameData/DnD5e_levels.json` (`LevelRules`): thresholds, proficiency bonus, hit dice, Ability Score Improvement levels. The "Class" stat carries no level (`ClassLevel` strips the "lvl 3").

"Level up" (`LevelUpDisplay`, F10 or the button on the card, outside combat) is a panel on the "Character" tab instead of the dialog: the card at the top shows the hero after the level-up (`HeroDisplay.Preview`, changes in gold), and the picture area shows a spell card from the reference or the description of an option. There is no free input into the sheet: the code computes hit points (average or a single roll), the proficiency bonus and Ability Score Improvements; `LevelUpOptions` returns what was gained and the choice groups, and the input line is a request for your own option (same prompt, only within the class and level rules, a "★" option or a refusal); `LevelUpApply` is the sheet patch for the choice. `LevelUp.Apply` builds the new hero on a copy: from the patch only stats, skills, features, resources and spells, with the engine's numbers on top. "Accept" replaces the hero; cancelling changes nothing. The DM's options and the hit-point roll are kept in the save (`hero.levelUpDraft`, engine-only): cancelling, returning to the menu and restarting do not request them again and do not allow rerolling hit points.

## Maps and time

Locations are built in chunks: the DM sets the plan via `plan_location`, the code builds the geometry, and `PopulateChunk` fills the following chunks. There are dungeons, caves and open terrain. Without a location F1 is unavailable and the game continues on the world map or in a narrative scene.

`WorldLibrary` stores the world's geometry and chronicle. `GameWorld` links it to the hero's knowledge. `TravelService` computes the route, time and encounters; the DM receives a summary. More: [world-map.md](world-map.md).

`TakeRoundBundle` combines start-of-round effects and events. Combat movement is computed by `calculate_movement`; the DM decides what enemies do. The combat rules live in `SendAction/systemPrompt.md`.

## Files

- `NaviDnD/Prompts/{RuleSet}/{Action}/systemPrompt.md` — DM instructions.
- `GameData`, `icons`, `NaviDnD/sound`, `NaviDnD/Fonts` — application resources.
- `NaviDnD/Storage/settings.json` — settings; `gameN.json` — saves; next to them, fog of war and notes.
- `Storage/Worlds`, `Heroes`, `Portraits` — personal libraries; `logs` — diagnostics. All of this is excluded from Git.

Directories and settings are created on first launch; other data as it is used. An empty launch does not create a dummy saved game. Migration of old saves is kept.

`AppConfig.ProjectRoot` is determined from the current assembly: when running from source the data lives in the game project, in logic tests in the test project. Codex settings: [codex.md](codex.md). Checks: [testing.md](testing.md).

# Localization: English and Russian

The language is chosen in Settings (English/Russian) and covers the interface, the DM's responses, the reference data and the voice-over. **English is the source and default language**: every string in the code, every prompt and every service instruction to the DM is English. Russian is an optional translation kept in `ru.json` files. Existing saves keep working.

## Current state

- `L` (`NaviDnD.Data/L.cs`): the translation key is the English string in the code — `L.T("SETTINGS")`, templates `L.F("Day {0}", day)`, numbers `L.Plural(n, "day", "days")`. The Russian translation lives in `NaviDnD.Data/Localization/ru.json` (embedded in the assembly); with no translation, or in an English game, the English string is shown.
- Plurals: English takes two forms in the call; Russian takes three (1, 2–4, 5+) from `ru.json` under the key `"one|many"`, e.g. `"day|days": "день|дня|дней"`.
- Context suffix: when one English word needs several Russian translations, the key carries a suffix after `##` (`"north##to"`, `"EXIT##verb"`, `"NAME##person"`). The suffix is never shown — `L` strips it for English display.
- The installer has its own `NaviDnD.Installer/ru.json` (only its strings; a test checks it against the game's).
- Service instructions to the DM (summaries, MCP tool rules) are English and don't go through `L`; the DM replies to the player in the game's language.

## Key decision: interface language and world language

- **Interface language** — the `Language` setting in `AppConfig`, can be switched at any time. Default: English.
- **World language** is written into the world when it is created and never changes: names, descriptions, journal and history are already generated in it. A new world takes the interface language by default. Old saves without the field are treated as Russian — they really are.

This way saved text never needs translating, and changing the language doesn't break a game in progress.

## Stages

The work was done in stages while Russian was still the source language (Russian strings were the keys, English lived in `en.json`). The final step flipped the contract: English became the keys and the default, and the dictionary became `ru.json`.

### 1. Infrastructure and setting — done

- `L` with interface and world languages, an embedded dictionary, and a fallback to the key.
- `AppConfig.Language` (`"en"`/`"ru"`, in `settings.json`), a **LANGUAGE** item in Settings — the items and title rebuild immediately, other screens from the next redraw.
- `WorldMap.Language` — the interface language at world creation; the game's language is its world's language (stage 4).
- `LocalizationTests`: every string from `L.T/L.F/L.Plural` and `L.W/L.WF/L.WPlural` has a Russian translation, `{0}` placeholders match, keys are plain literals (not `$"…"`), reference names (biomes, place types, terrain, furniture, directions, parts of day) are translated.

### 2. Interface translation — done

- Menus, settings, updates, "My games", the new-game wizard, the hero questionnaire, the character sheet, journal, monster and spell cards, map legend and hints, dialog and rolls, AI and voice-over error messages, and the installer are all translatable.
- **Boundary:** the interface language covers only what is shown on screen and not saved, plus system hints in the dialog ("The way is blocked", "Combat is on — you can't set off"). Text that goes to the DM or is saved as part of the story is in the world's language (stage 4). Shared functions come in pairs: `MapConfig.FloorName` (for the DM) and `FloorLabel` (for the screen), `TravelService.PaceName` and `L.T(...)` when shown, `DaysLabel`.
- Hero and world data (race, class, stats, skills, place names) are shown as is — they are in the world's language.
- Installer: language from `Storage/settings.json` of the installed game, otherwise from the Windows language. Its own dictionary `NaviDnD.Installer/ru.json` (only its strings, a test checks it against the game) — so edits to game text don't rebuild the installer; `NaviDnD.Data/L.cs` is part of the installer's source fingerprint (`build-release.ps1`).

### 3. Keys instead of words and save migration — done

- Persistent keys (`NaviDnD.Data/Models/Keys.cs`): part of day `night/morning/day/evening` (`PartsOfDay`), special hero stats (`HeroStat.Key`: `race`, `class`, `alignment`, `background`, `armorClass`, `darkvision`, spellcasting — `StatKeys`), resource categories `abilities/spells`, passage rooms — the `Room.Passage` flag. Code compares only keys; `Storage` normalizes the model's words in either language to keys after every patch (and on load — the same path).
- AI context labels are neutral: `inspiration:true`, `disengaged:true`, `dead:true`, `light:20ft`, `(lvl 3)`; prompts use `partOfDay` and `category` as keys.
- **Migrations** (`NaviDnD/Migrations`): data files carry a `formatVersion` field (`StorageFormat.Current`). Old files are rewritten by `NaviDnD.exe --migrate <game folder>` — the update helper runs it after patches (and the installer over an existing install), only if the installed version is not older than the helper. Fallback — on game start (build from source, failed update). Before writing — a copy in `Storage/Backup/format-<N>-<time>`; writing goes through a temporary file; a file that fails to convert stays as it was, the error goes to `logs/migrations.log` and `Storage/update-error.txt` (visible in "UPDATES"). Running again changes nothing.
- Migration 0→1: stat and category keys, part of day (saves, notes, the hero inside library cards), `passage` on corridors/crawlways/galleries (saves and world locations), `language: "ru"` on games and worlds, notes become an object `{formatVersion, notes}` instead of an array.
- Questionnaire options (`heroOptions.json`) and "Adventure" step options (`adventureOptions.json`) are translation keys, shown in the questionnaire and sent to the DM in the world's language; random names have separate lists (`names` for Russian, `namesEn` for English).

### 4. The DM's narration language — done

- The game's language is `WorldState.Language` (from the world when a new game starts) → `L.World`; the new-game wizard sets the chosen world's language. The first line of every request is `lang:en|ru`; every system prompt has a language rule; `+` stress marks in SSML only with `lang:ru`.
- In the world's language (`L.W`/`L.WF`): world generator names (`WorldGenerator`: names, places, regions, islands — separate syllables per language), rooms (`BuildingGenerator`, `ChunkGenerator`), floors (`MapConfig.FloorName`), engine text in the story ("You go down the stairs", "I end my turn", target selection, "Body: …").
- Instructions to the DM in the context (summaries, MCP tool rules) are English service text; the model doesn't reply to the player with them.

### 5. Reference data — done

- Lookup by English and Russian name; MCP `find_spell/find_monster/find_item` return the name in the game's language (the MCP server takes the language from the save). The spell card is in the game's language (the reference has both languages).
- **Limitation:** monsters in the reference have only an English name — the bestiary entry (traits, actions) in `GameData/DnD5e_monsters_BD.dtn` stays Russian even in an English game.

### 6. Dialog highlighting — done

- `DialogHighlighter`: damage, healing, hit/miss, success/failure — in both languages; English creature names without grammatical cases (`Goblins`, `goblin's`), Russian ones with case endings.

### 7. Voice-over — done

- The voice follows the game's language. The English Silero model `v3_en` (57 MB) is a game file, `NaviDnD/Speech/models/v3_en.pt` (in the repository; reaches players once via a patch, file checks as for the rest); the Russian model is `v5_5_ru.pt`. English voices were picked by synthesized pitch (male `en_57/en_31/en_20`, female `en_21/en_11`; characters `en_23`, `en_6`); setting **ENGLISH VOICE**.

### 8. Documentation and release

- `README.md` is English. Shipping is a regular patch; the installer is rebuilt (its sources and `L.cs` changed), so the update helper is downloaded again and runs the migration.

## Checks

- `dotnet test NaviDnD.Tests/NaviDnD.Tests.csproj`: dictionaries (`LocalizationTests`), migrations (`StorageMigrationTests`), generator language (`WorldLanguageTests`), highlighting (`DialogHighlighterTests`), the `lang:` line (`SpeechTests`).
- The migration was checked on a copy of a real `Storage` folder (197 files): no errors, running again changes nothing.
- Not yet checked: a live game with the model in each language (`PromptTests` spend usage limits — run selectively), installing an update on a real build.

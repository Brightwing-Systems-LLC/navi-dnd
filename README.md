# 🐉 NaviDnD

**A tabletop RPG where the Dungeon Master is an AI.**
Create a hero, explore the world and fight on a tactical map. The DM runs the story, plays the characters and keeps the D&D 5e rules.

## About this fork

This is an English translation fork of [NaviDnD](https://github.com/AlexandrNemashkalo/navi-dnd), created by its original author ([navitalevich1 on GitLab](https://gitlab.com/navitalevich1), [original releases](https://gitlab.com/navitalevich/navi-dnd/-/releases)). The fork is maintained by Brightwing Systems.

- The whole project — code, AI prompts and documentation — has been translated to English. English is now the source and default language; Russian is kept as an optional translation (`NaviDnD.Data/Localization/ru.json`), selectable in Settings.
- The game remains under the original author's non-commercial license: [LICENSE](LICENSE) (governing, in Russian) and [LICENSE.en.md](LICENSE.en.md) (unofficial English translation).
- Remaining limitation: in the bestiary (`GameData/DnD5e_monsters_BD.dtn`) only monster names have English versions; the lore and stat-block text (traits, actions) is still Russian-only.

### [⬇️ Download the latest version](https://gitlab.com/navitalevich/navi-dnd/-/releases/permalink/latest)

[All releases](https://gitlab.com/navitalevich/navi-dnd/-/releases) · [What's new](RELEASE_NOTES.md) · Windows x64

![A combat turn: the hero tries to intimidate goblins, rolls the d20 on screen, the DM narrates the result](docs/media/en/combat_turn.gif)

| Your roll, not the AI's | World map |
| --- | --- |
| ![Intimidation check: d20 roll against DC 13](docs/media/en/dice_roll.png) | ![World map with regions and the hero's location](docs/media/en/world_map.png) |

**Leveling up** — pick hit points, subclass and spells; the character sheet shows every change in gold before you accept.

![Level up: the druid chooses a circle, changes are highlighted on the character sheet](docs/media/en/level_up.gif)

| Character sheet | Spell card from the manual |
| --- | --- |
| ![Character sheet](docs/media/en/character_sheet.png) | ![Thunderwave spell card](docs/media/en/spell_card.png) |

---

## ✨ Features

- 🎲 **AI Dungeon Master** — Claude or OpenAI Codex runs the game by the D&D 5e rules
- 🗺️ **Tactical and world maps** — grid combat and travel between locations
- 📜 **Journal and character sheet** — everything about your hero and adventure at hand
- ⬆️ **Experience and levels** — the DM awards XP, you choose how your hero grows
- 🌐 **English or Russian** — interface, story and generated names; choose in Settings
- 🔊 **Voice-over** — the narrator reads lines aloud, offline
- 🔄 **Auto-updates** — the game downloads only changed files

## 🚀 Getting started

1. Download the installer from the link above and run it.
2. Sign in to [Claude Code](https://code.claude.com/docs/en/quickstart) or [Codex CLI](https://developers.openai.com/codex/cli) — the game finds them itself.
3. Choose **New game** and create a character. To play in Russian, set **LANGUAGE** in **Settings** first.

---

For developers: [build, tests and releases](docs/development.md).

**License:** free for non-commercial use with attribution. Selling or commercial use only with the author's written permission. Details — [LICENSE](LICENSE) ([unofficial English translation](LICENSE.en.md)).

# Open tasks

Carried over from the former development log. Observations need to be reproduced on the current build; completed items and outdated plans have been removed.

## Game correctness

- Check and fix the creation of feature usage counters, their spending on use, and recovery after a short rest for the features that get it under the rules.
- Distinguishable names for identical creatures, so the DM doesn't mix up individuals.
- Keeping descriptions consistent with mechanical patches and movement.
- Checking the order in which effects expire at a round boundary and the turn returns to the hero.
- Stability of the door adjacency tests and of switching between map and world.
- Undelivered travel: accounting for food and time together with the story of the road.
- Placing the hero inside the inn when staying the night; known places on a distant map.

## Checks

- Stealth, surprise, multi-step combat turns with real models.
- Planning and populating dungeon/cave/outdoor, passability of creature cells.
- Night visibility, darkvision, light and contrast on different monitors.
- Legend overflow and its display when the camera moves.
- Journal: quest steps, statuses, places and manual notes.
- A full UI run after changes to the menu, tabs and character editor.
- Applying models, paths and sound after changing settings.
- Auto-discovery of Claude/Codex on players' machines: stale paths, npm, editor extensions and Claude Desktop; messages about missing sign-in, access and limits when creating a world and the hero description.
- Two-finger scrolling on a real laptop touchpad, including smooth short gestures.
- Minimizing while waiting, typing and rolling: no sound in the background, a taskbar notification, restoring the fullscreen window and the voice-over of the current line.
- Area shapes, visibility and distances in `TargetGeometry`.

## Development

- Objects that block movement and visibility; path and straight-line distances in the tooltip.
- Floors, windows, special transitions and portals.
- Key remapping; switching screens while the hero editor is responding.
- A full-fledged companion and multiplayer.
- Levels: multiclassing; checking the `LevelUpOptions`/`LevelUpApply` options with real models.
- English text for the monster reference (`GameData/DnD5e_monsters_BD.dtn`): currently only the name is in English — the bestiary entry and the monster card in an English game stay Russian. Add English fields (size, type, alignment, traits, actions, reactions, legendary actions) and show them according to the world's language (`MonsterInfo`, `MonsterCard`). Details: [localization.md](localization.md).
- Music mood and positional sounds for sources on the map.
- Pinning a voice-over voice and timbre to each NPC on the code side (not chosen by the model), taking the NPC's gender into account: the same character always sounds the same.
- Reducing AI latency while keeping the game rules; showing finished entries as they arrive.
- A possible MCP tool for effects once a common contract is defined.

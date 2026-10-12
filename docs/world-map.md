# World map: how it works and what the DM needs

## How the player interacts with the map

| What the player wants | How it is done |
|---|---|
| Know where they are | The **[F2] World** tab: the camera is on the hero (purple pixels, like the start marker), the legend shows "■ You: place · kingdom", hovering the hero shows "You are here". Without a tactical map "Continue" opens the world right away and **[F1] Map** is grayed out. Journal **[F3] → [F8] Locations**: "You are here" / "You've been here" / "Known by hearsay". |
| Look around the world | Drag, wheel/Ctrl±, arrows; filter [F6]–[F9]; Tab cycles places in view; hovering shows a card for the place, terrain, road or bridge. |
| Go to a known place | Click a place → dashed route, the legend shows "Route: X — N days, by road Y%" → **[F10] set off**. **[F12]** sets the pace. |
| Go into the unknown | Click any cell (forest, mountains, coast) — same route and [F10]. On arrival the DM decides what is there (and may add a place). |
| Switch between the local area and the world | **[F1] Map** (the local area, if any) / **[F2] World** — by key or by clicking the tabs. |
| Leave a location | Step out through the purple exit by the location's entrance. The location is kept with the place, the World tab opens, Map becomes gray. |
| Return to a place | Travelling to a place with a saved location brings back "everything as it was" (NPCs, items), with the hero at the entrance. |

## Travel (D&D 5e)

The mechanics are computed by code (`TravelService`); the DM describes the road in a single call (`GameAiClient.Travel`, the `## TRAVEL` section of the message):

- **Time.** A world cell is a quarter day of travel (`WorldAtlas.CellDays`): on a road ×0.75, off-road longer depending on terrain (plains ×1.5, forest ×2, mountains and swamp ×4) — routes stick to roads; a ford adds a quarter day. Departure is in the morning (in the evening/at night — the next morning), arrival in the evening.
- **Pace.** Fast — +25% per day, −5 to passive Perception. Slow — −25%, allows stealthy travel, fewer hostile encounters.
- **Encounters.** One roll per day of travel based on the region's danger: a highway within a kingdom — 5%, off-road — 9%, no-man's land — 14%; swamp, mountains, deep forest — +3%. Type: hostile 25% (slow pace — 18%), peaceful 45%, discovery 30%. Hostile encounters are rare and "within the hero's strength, avoidable", so the game doesn't feel stifling. An encounter interrupts the journey where it happened.
- **Supplies and rest.** The travel summary includes a "Food" line: food in the inventory with id and quantity, and food inside kits (which is split out as a separate item). The DM deducts one ration per day; if there isn't enough — one foraging roll (Survival DC 10) for the whole shortfall; surplus forage goes into the inventory. Camping is `long_rest`.
- **Consistency.** Days on the road are counted from the same accumulated time as the route estimate (what was shown is what elapses). The summary names the direction (`WorldAtlas.Direction`) and the destination by the region's name (`WorldAtlas.TileName`). Pending events of the scene left behind (`scheduledEvents`) are cancelled and listed for the DM.
- **Nothing is lost.** On server overload (529/503) the request is retried up to 3 times. If the DM never answers, the travel summary stays in `WorldState.PendingTravel` and goes to it with the player's next action.
- **Navigation.** If more than half the route is off-road — Survival DC 10–15, on a failure +1 day. Fords — Athletics DC 10.
- **Fewer calls.** One model call for the whole journey (or up to the encounter), not one per day.

## The tactical map — only when needed

- **Theater of the mind.** Without a tactical map (on the road, at the market, in a tavern for a conversation) the DM runs the scene in text. The `world:` line in the context is marked "no tactical map".
- **When to build a scene.** When tactics are needed (combat, a dungeon, searching ruins, a chase), the DM calls `plan_location`. Mid-game the MCP server writes the map into the save and the game re-reads it (`location_planned.flag`).
- **Terrain from the world map.** The theme of open chunks comes from the cell's biome (`GameWorld.SiteAt`): forest, plains, hills, mountains, swamp, desert, snow, taiga. A desert won't be drawn in the mountains. A river through the cell runs through the whole location (its width depends on the river's size; paths cross it by fords). A nearby sea or lake makes the edge of the location water.

## Several games in one world

- **The world file** (`Storage/Worlds/<id>/world.json`): geometry and the **chronicle** — the world's name and concept, kingdoms, all of the DM's places (description, secret, path, saved location `locations/*.json`). The chronicle is shared: the next game in this world sees the same concept and places, and the tavern "as it was".
- **A game** (`WorldState.World`): what its hero knows (`known`), where they've been (`visited`), where they are now (cell and place). A new hero knows the capitals and whatever they discover themselves.
- **New game step by step** (`NewGameWizard`): 1 WORLD — a new one (map right away, the concept from the model via "INVENT") or a world that has already been played (map and chronicle); 2 HERO — a new one or from the hero library (personality, starting at level 1) → the questionnaire; 3 ADVENTURE (after the editor) — wishes and, optionally, a starting place picked by clicking the map.

## What the DM needs to run this kind of game

1. **Where the hero is and what is around** — the `world:` line (place, kingdom, terrain, river, sea, road, nearby natural region).
2. **Facts about the world, not inventions** — `world_query`: `place:` (description + secret), `near:` (what's nearby, direction, days of travel), `route:` (how long to go, through which kingdoms), `overview`.
3. **Growing the world with the story** — `add_place`: a village an NPC talked about; ruins the hero was sent to; a hidden plot (`hidden:true`) that will be revealed later. The code picks the cell from the wording ("3 days north", "in the mountains", "by the river"), and a path is laid to the village or lair.
4. **The world concept at the start** — briefly: name, kingdoms (culture, ruler, relations), capitals. The rest as the game goes.
5. **Consistency** — in a world with a chronicle the DM doesn't change the concept and places but continues them.
6. **A scene on demand** — `plan_location` only for tactics; terrain comes from the world map.
7. **Travel as a scene** — the engine's summary (days, terrain, rivers, places along the way, encounter); a story of 2–4 entries without recounting every day.

## Next (ideas)

- Weather and seasons — effect on travel and encounters.
- Region danger driven by the story (war, plague) — the DM marks a region, the code factors it into encounter chances.
- Transport: horse, cart, ship (ports) — a different pace and routes over water.
- Factions and rumors by kingdom — `world_query` with news.
- Player marks on the world map (personal notes, like notes in the journal).

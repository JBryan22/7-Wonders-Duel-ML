# Reading the game from Tabletop Simulator

Type: research
Status: resolved
Blocked by:
Research: `docs/research/tts-mod-and-api.md` (on main)

## Question

How can an external program read the state of a 7 Wonders Duel game in Tabletop Simulator (and, later, act on it)? Specifically: (1) what TTS offers — the External Editor API (localhost TCP), Lua scripting in mods, `WebRequest`, object GUIDs/tags/zones/descriptions — and its limits on Windows; (2) what is publicly knowable about the "7 Wonders Duel [ULTIMATE ed.]" mod (Workshop 3368311210): whether it is scripted, how cards/wonders/tokens/coins/military pawn are represented and identified, and whether it already tracks game state; (3) whether we can attach our own script without breaking the mod.

Deliverable: findings doc in `docs/research/`, noting what can only be confirmed from the mod's save JSON (see "Export the TTS mod save file").

## Answer

Full findings: `docs/research/tts-mod-and-api.md`. The mod JSON was downloaded via the Steam Web API and decoded.

- The mod (author `z|fn@b`, updated 2024-12-15, derived from Workshop 2115173088) covers base, Pantheon, Agora, solo, Leaders and promos. Its scripts only do **setup and scoring**: no move or turn tracking.
- Pieces are identified **by name and tags**, not IDs. Cards and wonders use their English name with colour or `wonder` tags. Progress tokens use the `progress token` tag. Coins are named tokens `1/3/6 Gold`, counted per player area. There is one Conflict Pawn. The Global script holds a table of about 110 named zones and pawns with their IDs. Red sits left and White right (still unconfirmed).
- **Read path, no changes to the mod:** the External Editor API (`localhost:39999` to send, `:39998` to receive) runs read-only Lua against Global. The editor plugins can't run during play because they also use port 39998.
- **Event path:** add our own scripted object that handles drop and zone-enter events. Its handlers must return nothing, and we must never replace the mod's UI or use Save & Play.
- **Hidden-info hazard:** Lua can read the names of face-down and removed cards, so the snapshot must filter them out to produce a proper Observation.
- Spawned coins get new IDs, so identify pieces by name, tag and position.
- Still unconfirmed (needs Windows): whether Lua `WebRequest` can reach localhost, where cards and discards sit during play, which seat is Red, military track x positions, how layout zones behave, and autosave frequency.

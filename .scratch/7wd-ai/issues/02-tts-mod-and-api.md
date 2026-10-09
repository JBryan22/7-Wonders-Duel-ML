# Reading the game from Tabletop Simulator

Type: research
Status: open
Blocked by:
Research: branch `research/tts-mod-and-api` → `docs/research/tts-mod-and-api.md`

## Question

How can an external program read the state of a 7 Wonders Duel game in Tabletop Simulator (and, later, act on it)? Specifically: (1) what TTS offers — the External Editor API (localhost TCP), Lua scripting in mods, `WebRequest`, object GUIDs/tags/zones/descriptions — and its limits on Windows; (2) what is publicly knowable about the "7 Wonders Duel [ULTIMATE ed.]" mod (Workshop 3368311210): whether it is scripted, how cards/wonders/tokens/coins/military pawn are represented and identified, and whether it already tracks game state; (3) whether we can attach our own script without breaking the mod.

Deliverable: findings doc in `docs/research/`, noting what can only be confirmed from the mod's save JSON (see "Export the TTS mod save file").

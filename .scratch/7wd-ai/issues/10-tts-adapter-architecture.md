# Tabletop Simulator Adapter architecture

Type: grilling
Status: open
Blocked by: 02, 03, 06

## Question

How does the TTS Adapter get the Game State out of the "ULTIMATE ed." mod and turn table changes into Moves — what runs inside TTS (Lua), what runs outside (C#), how they talk, how recommendations are shown back to the player in TTS, how it copes with unscripted/manual piece movement and desyncs, and what it must expose so an AI-controlled seat can be added later?

## Context from "Manual-entry Adapter: how it looks and behaves"

The Advisor Window (variant B, a local web page with a clickable board) is where the owner sees recommendations, whatever the source of the Moves. The TTS Adapter's job is to feed it: setup Reveals and Moves arrive automatically, and the owner corrects any misses by clicking. Advice inside TTS itself is optional.

Still to settle here:
- the transport between TTS and the Advisor Window;
- live events vs snapshots;
- reconciling a manual correction with TTS;
- joining a game mid-way.

## Context from "Solution structure and Engine testing strategy"

- The Lua scripts for the mod live in a top-level `tts/` folder.
- The C# side of the Adapter plugs into the `Advisor` project, which serves the Advisor Window as plain HTML/JS.

## Context from "Capture a mid-game TTS save and verify unknowns on Windows"

- **Both transports work:** Lua `WebRequest` reaches a localhost server, and External Editor API pulls work, but send results back through `sendExternalMessage` as JSON strings (no Execute Lua return value without `returnID`, and nested tables are dropped).
- **Placement:** the board layout (pyramid grid with covering shown by height, city columns, Wonder rows with face-down tucked cards, the discard deck on a tile, the removed-card decks) is recorded in that ticket's answer, with the save `docs/research/tts-mod/ml-practice.json` and `probe.ps1` available for testing.
- **Autosaves** every 5 minutes are too slow to serve as a live feed.

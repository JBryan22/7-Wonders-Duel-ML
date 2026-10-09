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

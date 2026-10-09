# Tabletop Simulator Adapter architecture

Type: grilling
Status: open
Blocked by: 02, 03, 06

## Question

How does the TTS Adapter get the Game State out of the "ULTIMATE ed." mod and turn table changes into Moves — what runs inside TTS (Lua), what runs outside (C#), how they talk, how recommendations are shown back to the player in TTS, how it copes with unscripted/manual piece movement and desyncs, and what it must expose so an AI-controlled seat can be added later?

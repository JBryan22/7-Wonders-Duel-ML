# Capture a mid-game TTS save and verify unknowns on Windows

Type: task (HITL)
Status: open
Blocked by:

## Question

The mod's starting setup is already public and decoded (see "Reading the game from Tabletop Simulator"). What's missing can only be seen during real play on the Windows machine:
1. Play into Age II with some cards built, at least one discard, one Wonder built, one progress token taken and the Conflict Pawn moved. Save, then commit the file from `Documents\My Games\Tabletop Simulator\Saves\` into `docs/research/tts-mod/`. Write down which seat (left/right, Red/White) you sat in.
2. Paste a provided Lua `WebRequest.get("http://localhost:...")` snippet into the game (an agent will supply it along with a tiny local listener) and record whether it reaches localhost.

Resolved when the save is committed and the two facts (seat colour, localhost reachability) are recorded in the answer.

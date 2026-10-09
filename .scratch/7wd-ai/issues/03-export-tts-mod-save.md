# Capture a mid-game TTS save and verify unknowns on Windows

Type: task (HITL)
Status: resolved
Assignee: Jesse Bryan
Blocked by:

## Question

The mod's starting setup is already public and decoded (see "Reading the game from Tabletop Simulator"). What's missing can only be seen during real play on the Windows machine:
1. Play into Age II with some cards built, at least one discard, one Wonder built, one progress token taken and the Conflict Pawn moved. Save, then commit the file from `Documents\My Games\Tabletop Simulator\Saves\` into `docs/research/tts-mod/`. Write down which seat (left/right, Red/White) you sat in.
2. Paste a provided Lua `WebRequest.get("http://localhost:...")` snippet into the game (an agent will supply it along with a tiny local listener) and record whether it reaches localhost.

Resolved when the save is committed and the two facts (seat colour, localhost reachability) are recorded in the answer.

## Answer

Done on 2026-10-09 on the Windows desktop (TTS v14.2.2). Assets in `docs/research/tts-mod/`:
- `ml-practice.json`: the save, taken in Age II. It holds no Steam IDs.
- `probe.ps1`: a reusable probe. It sends one read-only Lua script through the External Editor API and runs a tiny local HTTP server.
- `probe-results.json`: the probe's output.

**The two facts asked for**
- **Seat:** the owner sat **White, on the right** (hand at x = +23.6, host). Red is on the left (x = −23.6). This confirms that the mod's "left/right" means Red/White.
- **Localhost:** Lua `WebRequest` **reaches localhost**. GET to `localhost` and to `127.0.0.1`, and POST to `127.0.0.1`, all returned 200.

**API behaviour found while probing** (corrects "Reading the game from Tabletop Simulator")
- Execute Lua (ID 3) sent without a `returnID` **never returned a value** (no ID 5). Adding `returnID` is untested.
- `sendExternalMessage` (ID 4) works, but a payload with **nested tables is silently dropped**. Send flat values or `JSON.encode(...)` strings.
- **Autosave** runs every **5 minutes**, rotating through `TS_AutoSave*.json`.
- `onSave` state changes during play only in `currentAge` (`"Age2"`), plus the `layoutZones` options.

**Where things sit during play** (both seats face the table from the same edge, z = −60)
- **Pyramid (Age II):** card centres lie on a grid with **x step 2** (neighbours in a row are 4 apart) and **rows 2.5 apart in z**. The 6-card back row is at z = 10, the 5-card row at z = 7.5, and so on towards the front. **Covering is visible in `posY`**: covering cards sit higher. All 7 remaining cards are face up. The data fit the rules' coverage exactly: 13 cards taken, and Barracks plus the two 5-row cards are accessible. Age I's grid is not yet observed but is presumably mirrored.
- **Cities:** cards sit face up in columns by colour, left (Red) at x −17 … −5 and right (White) at x 4.7 … 17.2, z ≈ −14 … −12. The right columns match the right layout zones' x positions (4.69 brown, 7.82 yellow, 10.95 green, 14.08 blue, 17.21 red). Layout zones exist for both sides; their options are in `onSave`.
- **Wonders:** stand in columns at x ≈ −39.9 (left) and +40 (right), with rows at z ≈ −32.8, −27.0, −21.2, −15.6. A **built Wonder has its Age card tucked face down** about 3 units inwards (x ≈ ∓37), on the same z. Built here: Piraeus (Red); The Great Library, The Statue Of Zeus and The Temple Of Artemis (White).
- **Discard pile:** a **face-down Deck** of 7 cards on a locked `Custom_Tile` at about (−50.6, 2.3). Discards merge into that one deck.
- **Removed cards (hidden):** two face-down 3-card decks at about (−65, −25) and (−59, −25), one for Age I and one for Age II. Unused Wonders stay in deck `ce2da4` (4 cards). The Age III deck (20) and the Guild deck (7) are still unmixed. Lua can read all these names, so the snapshot must filter them out.
- **Progress tokens:** taken tokens sit in the owner's area (Theology and Agriculture at x 23.5 / 29.7, z −15.1). Four tokens remain on the board at z = 24.2, and four are in the bag. The Great Library draw explains the totals: 4 on the board + 2 taken + 4 in the bag = 10.
- **Coins:** loose `1/3/6 Gold` tokens near each hand edge (z ≈ −4). White has 7 here and Red has 3.
- **Conflict Pawn:** at x = 6.58, with the track centred at −0.19 and 44.7 wide. The right player's 2-coin looting token sits off the track at (20.4, 25.1). Mapping all pawn positions to track spaces needs more readings.
- Cards in the 33 Age I/II slots: 22 in cities, 4 tucked under Wonders and 7 in the discard pile, which matches 20 + 13 cards taken.

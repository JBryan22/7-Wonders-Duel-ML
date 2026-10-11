# 32: Reading the TTS table

**What to build:** The read-only Lua snapshot script, the External Editor API client, place mapping for all three Ages, hidden-card filtering.

**Blocked by:** 29

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] The ml-practice save gives a snapshot matching the probe findings place by place (in CI: the real Lua run in MoonSharp on the save; the live check in TTS is the owner's, below)
- [x] No hidden card name ever leaves TTS

## Notes

- **Files:** `tts/snapshot.lua` (the script), `tts/names.json` (TTS name → slug), `tts/places.json` (table geometry), `tts/fixtures/ml-practice.snapshot.json` (what the script gives for the committed save; rewrite with `UPDATE_TTS_FIXTURE=1 dotnet test`). All embedded in the Advisor. C# in `engine/src/Advisor/Tts/`: `TableSnapshot` (raw pieces), `TableView` (places), `TtsLink` (External Editor API client), `TtsData`, `TtsCommand`.
- **Split of work:** the Lua only reads and filters; it reports raw positions. C# turns them into places with `places.json` (spec: "places and the name mapping live in data"). Places: `Pyramid` (Engine slot index for the given Age), `City`, `UnderWonder` (row 1..4: that Wonder is built), `Wonder` (row 1..4, 1 = next to the board), `WonderOffer` (the Deal Wonders spots), `Discard`, `ProgressBoard`, `ProgressArea`, `LibraryDraw`, `ProgressBag`, `Storage` (box area: never input), `Hand`, `Loose` (off-grid, between areas, an accidental deck). Coins per side, pawn space (+ = towards White), looting tokens on board/taken are separate soft-check fields. Sides are TTS colours (Red left, White right); mapping colours to P1/P2 is ticket 34's seat detection.
- **Hidden-info rules in the Lua:** a card is named only face up or on the discard tile; a deck is named card by card only on the discard tile, otherwise just a count; a Progress token only face up and outside the Great Library draw strips (the mod's button deals the draw face up at z = −24; both players' draws stay nameless, the owner names theirs in the picker); nothing in a hand. **No GUIDs are sent**: the mod file fixes every card's GUID, so a GUID would identify a face-down card. Face-down cards report their back (`age`/`guild`/`wonder`), which is public. Tested on the save (every name sent is public, no hidden name or GUID in the text) and on 40 random tables against an independent C# statement of the rule.
- **Read-only, proved:** the tests run the committed Lua in MoonSharp (TTS's own interpreter) with TTS's sandbox modules against a fake table whose objects have getters only; any other call or assignment, or a new global, is an error. `sendExternalMessage` fails on nested tables (TTS drops them silently).
- **MoonSharp bug found:** a bare `local kind, value` inside a loop kept values from an earlier pass (a table and a number), so every non-game object was reported. The script now writes `= nil, nil`. Ticket 33/34 Lua: always initialise locals.
- **Name trap:** the mod's "Courthouse" is the Age III red card (our `pretorium`) and its "Tribunal" our `courthouse`; `GameData.SlugForName("Courthouse")` would be wrong. Always go through `TtsData.Names`.
- **Age I and III grids** come from the mod's own setup scripts (downloaded from the Workshop file, base version): slot centre x = 2·(layout column − centre column), z = 10 − 2.5·row, for all three Ages; snap ±1. Tested by dealing every slot at the mod's positions (nudged ±0.6) for each Age. The Age comes from the mod's `current_age` (read in Global) unless the caller passes one; ticket 33 should pass the Game State's Age.
- **Transport:** `TtsLink` listens on 39998, sends `{messageID: 3, guid: "-1", script}` to 39999, and matches the answer by a request id put in the script (`@REQUEST@`), outside the snapshot text, so an unchanged table gives an identical string (settled detection: also `TableSnapshot.StillNow` = nothing held or moving). TTS's Lua errors (message 3) are kept and shown on a timeout; a load-game message (1) sets `GameLoadedAt` for ticket 34. `returnID` was not needed and is still untested.
- **For ticket 33:** `TableView.From(snapshot, age)` then `At(place[, side])`, `OnSlot(slot)`, `WonderBuilt`, `Coins`, `PawnSpace`, `Looting`; `Facts()` gives sorted one-line facts and `TableView.Compare` a place-by-place diff (good Desync messages). The pawn step (2.2 units per space) is fitted to the mod's VP zones, so treat the pawn as approximate. Draft offers sit where the Deal Wonders button puts them (±4.5, −7/−13); the second round lands on the same spots.
- **CLI:** `7wd-advisor tts-snapshot [--compare FILE] [--save FILE] [--count N] [--age N] [--timeout S]` prints the table place by place and round-trip times. Not yet wired into the Advisor Window (that's ticket 33's polling).
- Tests: Advisor 50 (16 new: practice save place by place, committed fixture, no hidden names/GUIDs, random-table leak check, read-only, name table, three Age grids, offers/columns/Library strip, compare, link round trip, script failure, timeout with TTS error, TTS not running and busy port, the command's MATCH/DIFFERENT output), Engine 342, Agents 97, Python 48. Engine untouched: determinism hashes and frozen C++ games unchanged.

### Owner's live check (Windows desktop, about 5 minutes)

1. Start Tabletop Simulator. Close VS Code if it has the Tabletop Simulator extension running (it uses the same port).
2. Load the practice save: copy `docs\research\tts-mod\ml-practice.json` from this repository into `Documents\My Games\Tabletop Simulator\Saves\`, then in TTS choose *Games → Save & Load* and open `ml-practice`. Take a seat as White. Don't move anything.
3. Double-click `tools\tts-check.cmd` in the repository folder. A console window reads the table five times, one second apart.
4. Expected: five lines "Snapshot N: … ms round trip …", the list of places, and at the bottom **"MATCH: the table matches ml-practice.snapshot.json place by place."**
5. Tell Claude: MATCH or DIFFERENT, and the round-trip times (that answers the spec's "cost of 1 Hz polling"). If it says DIFFERENT, or anything else goes wrong, the full snapshot is saved in `runs\tts-live-snapshot.json`; leave it there for Claude.

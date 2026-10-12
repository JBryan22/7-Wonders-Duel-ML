# 34: TTS game life cycle

**What to build:** Seat detection, new-game detection, the promo Desync fix, restart catch-up.

**Blocked by:** 33

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Restart mid-game catches up cleanly (in CI: random games 6 Moves ahead of the saved log, and a server restart on a fake table; the live check is the owner's, below)
- [x] Unticked promo in a draft offers the one-click fix (in CI on a fake table read through the real snapshot.lua; live check below)

## Notes

- **Files:** `engine/src/Advisor/Tts/TableLifecycle.cs` (`OwnerSide`, `LooksFresh`, `MissingPromos`), `AdvisorSession.WithRules` (rules header rewritten, log replayed), server wiring in `AdvisorServer` (statuses `seat`, `new-game`, `promo`; `POST /api/tts/newgame`, `POST /api/tts/promo {promo}`), `ReconcileOptions.CatchUp`, `TtsAdapter`'s `loaded` callback (`ITableSource.LoadedAt`, from `TtsLink.GameLoadedAt`). Page: the TTS box also sits under the new-game form; "You sit" is Auto / White / Red. Tests: `TtsLifecycleTests` (10).
- **Seat:** `tts.json` `Color` is now `Auto` by default (old files keep their White/Red, which act as an override). Auto = the host's seat colour from the snapshot's players list. Host Grey/other/not seated → status `seat` asking, nothing read. The detected colour is live (a host changing seat mid-game flips the sides: a Desync until they sit back).
- **New game:** "fresh" = no Age dealt (`current_age` None) and nothing in cities, Wonder columns, under Wonders, the discard pile, Progress areas or Library strips (tokens on the board and the draft offer may be out). Offered when there's no game, or when a game is in progress and the table is fresh and doesn't match (Desync). One click (`newgame`) uses the remembered new-game settings (shown in the box; "other settings…" opens the form). Never automatic; a game under way with no log is still "no-game" (no cold join).
- **Promo:** after a Desync or Waiting, promo Wonders in the draft offer or a player's column that the rules leave out → status `promo` with one button per promo. The click rewrites the header, replays the log, re-reads the table at once, and ticks that promo in the remembered new-game settings. Unused promos in the box area (x ≈ −76.7) are ignored.
- **Catch-up:** on at server start, when following starts, on a new game, and when TTS says it loaded a game (message 1; settling also starts over and the hold after an Undo is dropped); off once the table matches. It searches 8 Moves deep (24 entries, 60k states, 120 readings per Reveal) instead of 4. Measured on random games: gap 6 → all followed, ~10 ms average, worst ~0.2 s; gap 8 → 2 of 60 Desync at the node limit (worst ~2.4 s, under the server lock). A longer gap gives a Desync saying to enter the missing Moves by hand. The search now skips states already searched, and Waiting prefixes are ranked by how few differences they leave (both also help normal following).
- **Inherent limits:** which card went under a Wonder is invisible once tucked, so a long gap containing Wonder builds may need the owner to pick or enter that Move by hand (random games build far more Wonders than people).
- **For ticket 35:** the combined live checklist covers the restart, seat and promo parts of P10's acceptance; the acceptance game itself needs the "best" network (`--agent neural:<onnx>`).
- Tests: Advisor 70 (10 new, in `TtsLifecycleTests`; the ticket 33 server test now expects "Auto"), Engine 342, Agents 97, Python 48. Engine untouched: determinism hashes and frozen C++ games unchanged. The Lua is unchanged (still read-only). Page checked in headless Edge with `--tts-file`: the new-game offer and the promo button, then the Messe enabled and the draft entered.

### Owner's live checks

All live checks for tickets 32, 33 and 34 are in one ordered list for a single TTS session: **[docs/tts-live-checks.md](../../../docs/tts-live-checks.md)** (about 40 minutes). This ticket's parts are steps 7–9 (seat and new-game detection), 14–15 (restart and reload catch-up) and 16–18 (the unticked promo).

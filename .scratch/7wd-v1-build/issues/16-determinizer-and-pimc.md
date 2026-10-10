# 16: Hidden-card guesser and PIMC

**What to build:** The uniform Determinizer and PIMC (50 x 1), searching from the Observation only.

**Blocked by:** 15

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] PIMC passes the adaptive balanced Heuristic
- [x] No-peeking test: same Observation with different hidden cards gives the same result under one seed

## Notes

- Determinizer: `Engine/Search/Determinizer.cs` (`new Determinizer(seed)`, `Reseed`, `Sample(observation[, into])`, and it is itself the sampled world's `IChanceSource`). Because the Game State never stores hidden identities (ADR 0002), a sampled world = the Observation's state with the opponent's hidden Great Library draw filled (3 of `TokensSetAside`, via the new internal `GameState.FillLibraryDraw`), and every later Reveal (flips, Guild-back slots, draft offers, returned cards, a Library draw) drawn uniformly from the unseen pools when it happens; that is distributed like dealing all unknowns up front. Same seed = same world and Reveals. A redacted (Advisor-logged) draw is filled the same way.
- PIMC: `Agents/PimcAgent.cs`, spec `pimc[:samples=50,playouts=1,margin=0.02]`. Per world, every legal Move gets the same playout seed (common random numbers); playouts are uniformly random for both sides. `Search(observation)` returns per-Move playouts, win rate, StdErr and mean final VP margin; `LastSearch` holds the last one. A Move a world rules out (opponent's Library pick outside the sampled draw) is skipped in that world.
- Deviation: ranking is win rate + 0.02 × mean VP margin (`margin=0` = wins only, margin still breaks ties). Measured vs `heuristic:balanced`, base, 200 pairs seed 0: wins only 52.5% (fails) → with margin tie-break 56.0% (51.1–60.8) → margin 0.02: **64.2% (59.4–68.8), Elo +102**; seed 1000: 65.5%. Margin alone: 55%. `samples=200,margin=0`: 77.4%, so noise is PIMC's main limit.
- Other Matches (default settings): with both promos 68.2% (63.5–72.6); vs Random 400-0. ~9.5 ms per Move on the desktop (16 threads, 400 games in ~11 s).
- No-peeking test: the only hidden card a base Game State can hold is the Library draw, so the test makes each Library draw two ways and checks PIMC's full statistics on the opponent's Observations match. Ticket 17's MCTS should reuse the Determinizer and the same test.
- Tests: 331 Engine (new `DeterminizerTests`), 59 Agents (new `PimcTests`, incl. the full 400-game pass Match).

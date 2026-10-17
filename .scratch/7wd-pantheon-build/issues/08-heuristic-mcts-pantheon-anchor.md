# 08: Heuristic, MCTS and the Pantheon Anchor

**What to build:** Heuristic values for Pantheon (P-A6), `--expansion pantheon` in `7wd match`, the Pantheon Ladder (Random = 0) and the Pantheon Anchor (chance-node MCTS, 1,000 simulations).

**Blocked by:** 07

**Status:** done

Spec: `docs/spec-pantheon.md` (P-A6), on top of `docs/spec-v1.md`.

- [x] Adaptive balanced Heuristic passes Random in Pantheon games; PIMC passes it; MCTS passes PIMC (Elo and victory-type mix in Notes)
- [x] The Heuristic activates Divinities in a sensible share of games (reported)
- [x] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

- **Heuristic** (`Agents/HeuristicPantheon.cs`, `HeuristicAgent` is now `partial`): activations are scored like any Move (apply, follow own choices incl. `offer`/Enki/Snake/Gate/Theater/Minerva..., evaluate) and also pay the AfterTake denial term (an activation leaves the opponent the same cards). New Pantheon-only terms in `PlayerValue`, reached only through `s.Pantheon` (null in base): waiting Divinities (static `DivinityValue` minus the player's space cost in coins, x0.3 in Age I / x0.45 later; a hidden face-down card = its Mythology's average), held Mythology (1.2 VP-eq) and Offering tokens (0.6 x coin worth), Minerva on the player's side of the pawn, Poliorcetics/Engineering/Mysticism. Grand Temples in `CardValue` (marginal 5/12/21); Sanctuary +3 and Divine Theater +4 at draft. New weight `pantheon=` (default 1; `pantheon=0` = blind to the terms): sweep vs default 0.5 -> 48.6 %, 2 -> 45.6 %, 0 -> 42.0 % (default beats blind 58.0 %, CI 53.1-62.7). Fixes: `GameData.Card/Wonder` instead of base arrays, `SpendableCoins` (Astarte) for affordability and coins, `DerivedFeatures.OpenChains` skips Temple cards (crashed).
- **`7wd match --expansion pantheon`** (`MatchSettings.Expansions`, default None so base headers/resume files are unchanged).
- **Pantheon Ladder** (`Ladder.Elo(spec, expansions)`, `MatchReport` uses the Match's scale; laptop, 20 threads, no promos, 200 pairs seed 0 unless noted): heuristic vs random 3,997-3 in 4,000 games (+1250, CI +1062-+1437; victory mix 2,744 civ / 1,232 mil / 21 sci); default `pimc` vs heuristic **49.0 % (44.1-53.9), fails** -> decision: the Pantheon PIMC rung is `pimc:samples=200` (`Ladder.PantheonPimc`): **68.2 % (63.5-72.6), +133** (pimc wins 270 civ / 3 mil / 0 sci; heuristic 79 / 38 / 10); `mcts` (1,000 sims) vs pimc:samples=200 **65.0 % (60.2-69.5), +108** (mcts 220 civ / 39 mil; pimc 127 / 10 / 2 sci; 2 draws). Recorded Pantheon scale: random 0, heuristic 1250, pimc 1243, pimc:samples=200 1383, **Pantheon Anchor `mcts` = 1491** (`Ladder.PantheonAnchorElo`). Speed on the laptop: mcts ~199 ms/Move, pimc:samples=200 ~250 ms/Move in Pantheon (base mcts on the desktop was ~32 ms): budget Pantheon Anchor Matches accordingly. With both promos heuristic beats random 400-0.
- **Activations:** heuristic vs heuristic, 200 Pantheon games: the Heuristic activated in 198/200 games (99 %), 3.18 per game; every Divinity used (aphrodite 68, hades 39, zeus 3, astarte 67, baal 66, tanit 71, enki 23, ishtar 21, nisaba 13, anubis 33, isis 30, ra 8, mars 33, minerva 46, neptune 29, gate 85). Presets keep their skews in Pantheon (vs random, 400 games seed 500): science 268/50/82, military 108/292/0, civilian 304/96/0, balanced 276/122/2 (civ/mil/sci).
- **Base unchanged:** the same base Matches with the old and new binaries give identical per-game results (heuristic presets 300 pairs with/without promos, pimc vs heuristic 60 pairs, mcts:sims=300 vs heuristic 40 pairs with promos); determinism hashes, `FrozenGames/`, Encoding v1 manifest pass untouched; base 100k soak 96,502 (152) / 3,395 / 103, messe,liberty 96,140 (135) / 3,715 / 145.
- **Tests:** 460 Engine, 116 Agents (9 new `PantheonAgentTests`: activation share, balanced passes Random on the Pantheon scale with/without promos, Pantheon values beat blind, `pantheon=0` changes no base game, every Pantheon decision answered legally without touching the Observation, preset skews, PIMC/MCTS play Pantheon games with promos, separate Elo scales), 83 Advisor; Python 73 passed, 3 skipped. The PIMC/MCTS pass Matches are too slow for CI (2.5 and 6.5 min on 20 threads) and are recorded here only.
- **For 09+:** the Heuristic uses the Engine's legal Moves for every Pantheon decision, so new decisions need no Agent work; the Pantheon Run's Matches should use `--expansion pantheon` against `mcts` (Pantheon Anchor) so reports land on the Pantheon scale.


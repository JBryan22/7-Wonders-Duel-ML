# 17: Tree search (the Anchor)

**What to build:** Chance-node MCTS at 1,000 simulations, returning visit shares, per-Move values with standard errors and the principal line.

**Blocked by:** 16

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] MCTS passes PIMC
- [x] Deterministic for a fixed seed
- [x] Anchor Elo recorded

## Notes

- MCTS: `Agents/MctsAgent.cs`, spec `mcts[:sims=1000,c=0.7,margin=0.02]` (`simulations=` also accepted). Single-observer ISMCTS = chance nodes sampled at the Reveal: each simulation samples a world with the Determinizer, walks one shared tree of Moves, and every Reveal on the way (and in the playout) is drawn by the Determinizer when it happens. The tree is **open-loop over chance** (a node = a Move sequence, whatever was revealed in between); availability counts make UCT fair when a Move's legality depends on a Reveal. One node expanded per simulation, then a uniformly random playout. Node stats per edge from the mover's point of view (handles play-again and follow-up decisions by the same player).
- Ranking (UCT and tie-break) = mean result + 0.02 × mean final VP margin, as in PIMC; the played Move is the most visited root child. Sweep vs PIMC (400 games each): c=0.7 80.7 %, c=0.4 72.1 %, c=1.2 79.0 %, margin=0 77.7 %.
- Output: `Search(observation)` / `LastSearch` → `MctsResult(Moves, Best, Line, Simulations)`; per Move: visits, visit share, value (win rate, draw ½), StdErr (sample SD / √n), mean VP margin. Principal line = `LineStep(Player, Move, Visits, Value, AfterReveal)`, following the most visited child while it has ≥ 4 visits (`MinLineVisits`; fewer = "no line"); `AfterReveal` = a chance step usually precedes it ("face-down card revealed"). No outcome-class distribution (MCTS has no outcome head, spec says leave it out).
- Measured (desktop, 16 threads, base, 200 pairs seed 0): vs `pimc` **80.7 % (76.6–84.3), Elo +249**; seed 1000: 81.2 %; with both promos 86.0 %; vs `heuristic:balanced` 87.5 % (+338). ~30–34 ms per Move at 1,000 simulations (PIMC ~8–9 ms).
- Anchor Elo: `Agents/Ladder.cs` is the ratings store (Random 0, heuristic 1161, pimc 1263, **mcts = Anchor 1512**, chained from the pass Matches; heuristic vs Random re-measured 3,995–5 over 4,000 games = +1161, CI +1013–+1309, so the chain's error bars add up). `MatchReport` now uses these recorded ratings whenever `--elo-a/--elo-b` is not given, so `7wd match <net> mcts` reports the network on the Random = 0 scale directly. Only exact default specs are rated.
- Deterministic for a fixed seed and Observation (single-threaded per search; tested by replaying games and repeated searches). No-peeking test reused from PIMC (statistics and line match on two hidden Library draws).
- Tests: 331 Engine, 65 Agents (new `MctsTests`; the pass test uses 100 pairs to keep CI quick).

# 14: Match runner

**What to build:** 7wd match: seed pairs with seats swapped, win rate with 95% CI, Elo with Random = 0, victory-type breakdown, base-only vs promo Matches, wall-clock logging, resume from the last finished seed pair.

**Blocked by:** 10

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Random vs Random lands within its CI of 50%
- [x] A stopped Match resumes

## Notes

- `7wd match <a> <b> [--pairs N] [--seed S] [--promo P] [--out DIR] [--threads T] [--elo-a X] [--elo-b X]` (`Agents/Match.cs`, `Agents/MatchReport.cs`). Default 200 pairs = 400 games, base rules (`start-choice=new`), all cores. Exit code 0 when complete, 2 when stopped (Ctrl+C), 1 on bad input.
- Seed pairs: pair `i` uses `Rng(FirstSeed + i)` → one Chance Source seed shared by both games, then a seed for A and one for B (each Agent keeps its seed across the swap). Game 1 has A as P1, game 2 A as P2. Results depend only on the settings, never on thread count or stop/resume (tested).
- Agents come from `AgentSpecs.Create(spec, seed)`: `name` or `name:argument`, a fresh Agent per game (so Agents may hold per-game state and need not be thread-safe). Only `random` exists; tickets 15–20 add `heuristic:<preset>`, `pimc`, `mcts`, `neural:<onnx>` to `AgentSpecs.Factories`. `Match.Run(..., createAgent:)` overrides the factory (tests use a weak "discarder").
- Statistics: score = wins + ½ draws; 95% CI is the **Wilson** interval over games (sane at 0%/100%; treats games as independent, slightly conservative for paired games). Elo diff = 400·log10(p/(1−p)) with p kept half a game from 0/1; its CI comes from the Wilson bounds. Pass rule `APasses` = lower bound > 50%. Elo on the Random = 0 scale: `random` is always 0; any other Agent's known rating comes from `--elo-a/--elo-b`; the unknown side is derived from the known one (null if neither). A ratings store for the Ladder/Anchor is left to whoever needs it (ticket 17/24).
- Every game records A's seat, winner (A/B/Draw), victory type, both VP totals, Moves and decision wall-clock per Agent, and game wall-clock. The report shows the victory-type breakdown per side, A's wins per seat, this session's wall-clock, summed game time and ms per Move per Agent.
- Resume: with `--out DIR`, `DIR/match.jsonl` gets a header line (format `7wd-match-1`, agents, pairs, seed, rules header) and one JSON line per finished pair, appended and flushed as it finishes (pairs finish out of order; they are keyed by index). Rerunning the same command skips finished pairs; pairs in progress at a stop are dropped (cancellation is checked between Moves, so a stop is quick even for slow search Agents). A torn last line is dropped; a file from different settings (incl. a different `--pairs`) is refused. `DIR/summary.json` (win rate, CI, Elo, victories, timings) is written at the end of each session for `loop.py` to read.
- Measured on the desktop: Random vs Random, 400 games 51.9% (CI 47.0–56.7%), ~0.1 s; 4,000 games from seed 7: 50.6% (49.1–52.2%).
- Tests: 321 Engine, 46 Agents (10 new `MatchTests`: Random vs Random within its CI of 50% base and with both promos, 20k-game convergence, seat swap, a weak Agent fails with Elo < −200, thread-count independence, stop and resume equals an uninterrupted Match, torn line and foreign file, Wilson/Elo values, Agent specs).

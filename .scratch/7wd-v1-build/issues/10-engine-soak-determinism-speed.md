# 10: Engine soak, determinism and speed

**What to build:** Random-play invariants over 100k games, cross-OS determinism, and 7wd bench.

**Blocked by:** 09

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] All invariants hold over 100k games
- [x] Same seed gives byte-identical logs and state on Linux and Windows (CI)
- [x] bench at least 10k random games/s on one core (optimise if missed)

## Notes

- `7wd soak [--games N] [--seed S]` (default 100k from seed 0) runs `Agents/Soak.cs` on every core: after every log entry it checks coins ≥ 0, the game ends (< 1000 entries), a legal Move exists and every generated Move is legal, both players' Observations (opponent's Library draw hidden, otherwise equal to the state; same legal Moves for the decider), cached production = city, pawn on the track, looted tokens never return, tokens owned once, ≤ 7 Wonders; once per game a copy is played to the end and the original must stay byte-identical; at the end, cards-taken bookkeeping and Move Log replay (same state, same text). Seeds alternate `start-choice=new` (even) / `2015` (odd). A failure names its seed and rules.
- Measured on the Windows desktop (i5-12600KF, .NET 10.0.9): 100,000 games in 9.7 s on 16 threads, all invariants hold (96,502 civilian incl. 152 draws, 3,395 military, 103 science). CI runs 3k soaked games (`SoakAndBenchTests`).
- Determinism: `GameState.ToBytes()` is a canonical little-endian dump of every field `SameAs` compares. `DeterminismTests` pins SHA-256 hashes of 200 seeded games' Move Logs and of the state bytes after every entry, plus the readable start and Result of seed 0. CI runs it on ubuntu-latest and windows-latest, so drift fails on one of them. A deliberate rules or format change must re-pin the hashes (and say so in the commit); tickets 12/13 will not change them as long as base-game play is unchanged.
- `7wd bench [--seconds S]` (default 5 s per variant, one thread, after a JIT warm-up). Desktop, single thread: GameRunner (log + Observe) 20,000 games/s; Game State + Observe 23,600 games/s; Game State only 30,200 games/s (~72 decisions per game; 0.46 us/decision bare). So `Observe`'s copy costs ~0.13 us per decision (~25% of bare play); fine for now, but search should use `CopyFrom` into reused states rather than `Observe`/`Clone` per node.
- Optimisation: the first measurement was 15,700 games/s (GameRunner). Replacing the `yield` iterators over card sets, Wonder masks and token masks with an allocation-free struct enumerator (`Rules/Bits.cs`; `CardSet.Cards()`, `GameState.WondersIn`, `ProgressTokens.In` now return `Bits`) gave +25%. The `GameData` definitions did not need flattening. The laptop target (≥ 10k on one laptop core) has not been measured on the laptop; the desktop has 2x headroom. Self-Play positions/s arrives with Self-Play (ticket 22).
- Tests: 277 Engine (adds `BitsAndBytesTests`) and 17 Agents (adds `DeterminismTests`, `SoakAndBenchTests`).

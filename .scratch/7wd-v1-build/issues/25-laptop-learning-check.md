# 25: Laptop learning check

**What to build:** Short CPU Runs in classic then Gumbel mode.

**Blocked by:** 24

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Elo vs Random rises over the Generations in both modes

## Notes

- **Where it ran:** on the Windows desktop, CPU only, standing in for the laptop: each Run pinned to 8 of the 16 logical CPUs (4 cores + HT, `start /affinity FF` and `FF00`, the two Runs side by side), `OMP_NUM_THREADS=4`, every device `cpu`. To repeat on the laptop (from `training/`):
  `uv run sevenwd-loop ../runs/laptop-gumbel --config configs/laptop-gumbel.yaml && uv run python -m sevenwd.curve ../runs/laptop-gumbel` (then the same with `laptop-classic`; add `--opponent heuristic --every 2` for the second curve). ~10 min per Run here; expect up to ~2x on the laptop.
- **New:** `configs/laptop-gumbel.yaml` / `laptop-classic.yaml` (identical except the mode; classic Self-Play adds `noise=0.25,alpha=0.3,temp=1`, Matches have none): 128x2 net, 12 Generations of 400 games at 64 sims (~27k positions), window 100k, reuse 4, batch 512, Matches 50 pairs at 64 sims, `heuristic` as the Anchor every 4, stopping rules off. `sevenwd/curve.py` (`python -m sevenwd.curve RUN [--opponent X] [--pairs N] [--every K] [--sims S]`): plays every Generation against one fixed opponent on the same seeds (seed 1) and writes `RUN/curve-vs-X.csv` + `RUN/curve/`; exit 0 when the last Generation's lower 95% bound beats Gen 0's upper bound, 3 otherwise. Independent points, unlike the loop's chained Elo column.
- **Result: it learns, in both modes** (100 pairs per point, own mode at 64 sims):

  | Gen | Gumbel vs Random | Gumbel vs heuristic | Classic vs Random | Classic vs heuristic |
  |---|---|---|---|---|
  | 0 | 58.5% (+60) | 0.0% | 63.5% (+96) | 0.0% |
  | 2 | 99.0% | 11.0% (Elo ~800) | 98.5% | 9.5% (~770) |
  | 6 | 99.0% | 21.5% (~940) | 100% | 35.0% (~1050) |
  | 12 | 99.5% (~+920, capped) | 28.0% (~1000) | 100% (capped) | 45.5% (~1130) |

  Random saturates after Generation 1 (a 64-sim search with a barely trained net already wins ~99%), so the heuristic (Elo 1161) curve is the informative one: steady rise over all 12 Generations in both modes. Losses fall (total 1.96 → 1.57 Gumbel, 1.99 → 1.61 classic). Loop chain Elo at Gen 12: Gumbel 897, classic 1106.
- **Classic looked ahead, but it's the play-time search, not the training.** Cross-checks at Gen 12 vs heuristic: Gumbel-trained net with Gumbel search 28%, with `gumbel=0` 36.5%, with classic search **45.0%**; classic-trained net with Gumbel search 17.7%. Head-to-head, Gumbel-trained beats classic-trained 57.5% with Gumbel search and loses 46% (CI 39-53, a tie) with classic search. So the networks are about equal (Gumbel's slightly better), but at 64 sims Gumbel's sampled root choice plays weaker than classic's most-visited Move. **For 26/27:** check whether Matches/Advisor at 800 sims should use `gumbel=0` (or classic) — the Gumbel noise is for Self-Play exploration and may cost Elo in Matches; the classic-vs-Gumbel pilot should compare networks under one fixed evaluation search.
- **OneDrive fault (fixed):** twice in 24 Generations the Self-Play shard rename (`shard.tmp` → `shard`) got "access denied" from OneDrive holding the new files; the loop's retry-once saved both. `ShardWriter` now retries the rename for ~4 s. For the calibration/main Run, still put `runs/` outside OneDrive (or pause sync) — Runs write GBs.
- Speeds here (4 cores, 128x2 net): Self-Play ~400 games / 15-25 s, training ~430 steps of 512 in ~6-10 s, Generation 40-70 s.
- Tests: Python 48 (3 new in `test_loop.py`: laptop configs differ only in mode, curve verdict/slope, curve through real `7wd` on the tiny Run incl. resume and `--every`); C# Engine 342, Agents 94 (unchanged). Engine not touched: determinism hashes and frozen C++ games unchanged. Not done on the laptop itself: only the owner can rerun it there (command above).

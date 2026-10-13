# 26: Calibration run (Windows)

**What to build:** The calibration run on the desktop.

**Blocked by:** 25

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Speeds measured and recorded
- [x] Generations sized to about 1 desktop-hour
- [x] 300 hours buys at least 500k games, or raised with the owner

## Notes

- **Where:** the Windows desktop (i5-12600KF, 16 logical CPUs, 32 GB RAM, RTX 3060 12 GB, NVMe), Runs in `C:\7wd-runs` (`calib-a`: 2 Generations of 4,000 games with the spec defaults; `calib-b`: one full-size Generation with `configs/desktop.yaml`, Anchor Match every Generation; `calib-bench`: single-step benchmarks). Nothing is left running; only small logs/records remain there (shards deleted).
- **Measured speeds (256x4 net, 64 Gumbel sims, both promos):**

  | Step | Speed | One desktop Generation |
  |---|---|---|
  | Self-Play (CPU, 512 games at a time, 4 inference threads) | 20.4 games/s, 1,402 positions/s over 55,000 games (21.1-23.7 games/s in 2-4k-game runs); 68.7 searched positions/game | 55,000 games = 3.78M positions in 45 min |
  | Shard compaction (new, see below) | 3.78M positions in 208 s; 18 GB dense -> 2.2 GB | 3.5 min |
  | Training (CUDA, batch 1,024) | 47-53 steps/s, the same for 256x4 and 512x6 (batch building limits it, not the GPU) | 7,376 steps = 2.5 min |
  | ONNX export | < 1 s | - |
  | Match vs previous, 400 games at 800 sims (classic, CPU, 64 game threads) | 1.7 s per seed pair | ~6 min |
  | Match vs best (only when the best is older than the previous Generation) | same | +6 min |
  | Match vs the Anchor (`mcts`, 1,000 sims) | 190 s | 3.2 min every 5th Generation |
  | Generation 0 vs Random | 204 s | once |

  Total ~57 min per Generation (~63 when the best lags): **55,000 games per Generation; 300 desktop-hours buy ~290-300 Generations = ~16M Self-Play games** (32x the spec's 500k sanity check). First signal: after one such Generation the network already scored 21% vs the Anchor (calib-b; 8% after a 4,000-game Generation in calib-a).
- **Other measurements:**
  - Self-Play at 32 sims is *not* faster (1,585 vs 1,623 positions/s on 2,000 games: per-position overhead dominates), so a 32-sim pilot is pointless; 128 sims gives 763 positions/s (half).
  - Medium net (512x6) Self-Play: 696 positions/s on the CPU, **1,591 on CUDA** (= the Small net on the CPU). Medium Matches at 800 sims: ~6.6 s per pair on CUDA, 7.3 on the CPU (~23 min per 200-pair Match).
  - Matches: CPU beats CUDA at Match batch sizes (52 s vs 132 s for 32 pairs); 64 game threads ~18% faster than the default 16 (32 / 128 ~ same as 64); ORT threads 4 > 2 > 1.
  - Match search at 800 sims (calib-a Gen 2 against itself, 100 pairs): **classic beat Gumbel search 61.5% (CI 54.6-68.0)**; `gumbel=0` didn't help (46%). Same speed. So `desktop.yaml` Matches use `mode: classic` (most-visited Move, no noise); Self-Play stays Gumbel. The Advisor should use the same evaluation search (`neural:<onnx>,mode=classic`) — for tickets 28-29.
  - Shards are ~4.8 KB/position dense (2,417 float16 inputs, 94% zeros). A 1-hour Generation is 18 GB dense, so the spec's "~7 Generations" window (~26M positions) would be ~125 GB and far beyond RAM.
- **Built (the calibration showed this bottleneck; spec P8 allows it):**
  - `shards.compact_shard`: after Self-Play the loop rewrites the shard's dense `input.npy` as CSR (`input_offset/index/value.npy`), identical numbers, ~600 bytes/position (8x smaller), crash-safe (sparse files complete before the dense file goes). `ShardDataset` reads both forms; sparse batches are faster (6.7 vs 11.6 ms). It reads with plain chunked reads, not a memory map: the first version memory-mapped the 18 GB file and ran the desktop low on memory (the calib-b loop was killed mid-compaction and resumed).
  - Game Records are gzipped (`games.jsonl.gz`, ~4.5x smaller) when their Generation's shard leaves the window; `read_records` streams either form (a Generation's records are ~450 MB). Gunzip before `7wd selfplay-check` / `7wd shard`.
  - Tests: Python 51 (3 new: compacted = dense batches, compaction resume/refusal, desktop config; window test now checks the gzip; smoke test checks compaction); C# Engine 342, Agents 97, Advisor 70 (unchanged).
- **`training/configs/desktop.yaml`** (commented): 256x4, 55,000 games at 64 Gumbel sims on the CPU, window 25M positions (~7 Generations, ~16 GB compacted: fits in RAM), reuse 2, batch 1,024, Matches 200 pairs at 800 sims **classic**, CPU, 64 game threads, Anchor every 5, stop rules as the spec (plateau 20 Generations = 20 hours / 4 Anchor Matches; warm-start triggers at Generations 6-10 and 20 = hours, not days, which is fine). Disk: ~35 GB at any time + ~30 GB of gzipped records over 300 hours; the dense shard being written each hour is 18 GB (~5 TB of SSD writes over 300 hours; acceptable, but having C# write sparse shards directly would remove it).
- **Caveat seen once:** in calib-b's resumed part (started with `7wd-train start`), training ran at 17.5 steps/s and the Match vs previous took 807 s, with the first quarter of the Match at 4.1 s per Move, fading to normal; rerun in the foreground and as a detached hidden process both gave normal speeds, so it was outside load (likely Defender / Search indexing scanning the 20 GB of files just written, or the machine in use). Suggest excluding `C:\7wd-runs` from Windows Defender real-time scanning and Search indexing (needs admin: the owner). `generations.csv` (`train_steps_per_s`, `seconds`) shows such slowdowns.
- **For ticket 27 (pilots) — recommended plan, 20 desktop-hours each, compared on Anchor Elo at equal desktop-hours:** sibling Runs of `desktop.yaml` with `anchor_every: 2` (more curve points, +1.6 min/Generation), `cap_hours: 20` and the plateau/warm-start rules off.
  1. **Small/Gumbel baseline** = `desktop.yaml` (also the "derived features on" and "Gumbel" arm).
  2. **Medium** 512x6: `selfplay.device: cuda` (CPU is 2.3x slower), `matches.device: cuda`, ~40,000 games so a Generation stays ~1 hour (its Matches take ~23 min).
  3. **Without derived features:** needs a small build first (an Encoding option / version that zeroes or drops the derived-feature inputs; nothing in the config switches them off today).
  4. **Classic Self-Play** at 64 sims (`mode: classic`, `search: "noise=0.25,alpha=0.3,temp=1"` as in `laptop-classic.yaml`); Matches stay classic for both, so networks are compared under one evaluation search (ticket 25's point).
  5. Optional: 128 sims (~27,000 games per Generation). Skip 32 sims (no faster, see above).
  
  Total **80 desktop-hours** for 1-4 (100 with 5), inside the spec's 150; the rest is slack for a rerun or value-target mixing. Pilots 1, 2 and 4 can start now; 3 waits for its build.

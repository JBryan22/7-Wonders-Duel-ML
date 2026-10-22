# 15: Pantheon training Run (Windows)

**What to build:** After the base main Run has stopped: a short calibration, then the Pantheon Run warm-started by surgery from the base "best" network, until a stop rule fires (P-A8).

**Blocked by:** 11, and the base main Run (v1 ticket 28) stopped

**Status:** in progress (the Pantheon Run is training on the desktop, started 2026-10-22)

Spec: `docs/spec-pantheon.md` (P-A8), on top of `docs/spec-v1.md`.

- [x] Calibration numbers and the agreed cap/plateau in Notes
- [ ] The Pantheon "best" network passes the Pantheon Anchor

## Notes

### How to start the Pantheon Run from the main Run's "best" network (written by ticket 11)

Why this works: a Run keeps only one training checkpoint (`net/latest.pt`, its newest network), but it keeps every
Generation's exported network (`net/gen-NNNN.onnx`). Network surgery only needs the network's weights (the Pantheon
network starts with a fresh optimiser anyway), and the `.onnx` file holds exactly those weights, so surgery reads the
best Generation's `.onnx` directly. Nothing in the main Run is changed or needed besides that one file.

1. **Wait until the main Run has stopped.** Double-click "Training status": it must say stopped (cap or plateau) or
   paused, not running. There is one GPU; the Pantheon Run never runs alongside the main Run (P-A8).
2. **Get the Pantheon code** (only now, with the main Run stopped): in the repo folder, `git pull`.
3. **Find the best Generation:** open `C:\7wd-runs\main\best.json`; its `"generation"` is the number N
   (e.g. 23 means the file `C:\7wd-runs\main\net\gen-0023.onnx`).
4. **Make the starting network** (from the repo's `training` folder, in PowerShell):
   `uv run sevenwd-train surgery C:/7wd-runs/main/net/gen-00NN.onnx --out C:/7wd-runs/pantheon-init.pt`
   It must end with "same outputs as the parent in base-game positions". Keep this file outside the Run folders.
5. **Write the Pantheon config** `training/configs/pantheon-main.yaml` (Claude on the desktop does this): a copy of
   `main.yaml` with these changes, and nothing else:
   - `encoding_version: 2` and `expansion: pantheon`;
   - `init: C:/7wd-runs/pantheon-init.pt` and `init_from: null` (surgery start, not a parent-Run warm start; the
     main Run's shards are Encoding v1 and can't be trained on);
   - `matches.generation0_opponent: mcts` (the Pantheon Anchor, Elo 1491 on the Pantheon scale);
   - `matches.reference`: `mcts:sims=20000` has no Pantheon rating yet. Either rate it first
     (`7wd match mcts:sims=20000 mcts --expansion pantheon ...`, then set `reference_elo`) or set `reference: null`
     for the calibration;
   - `stop`: `science_min_share`, `science_from/to` and `progress_by` are base-game warm-start triggers: set them to
     `null`; `cap_hours` and `plateau_generations` are agreed with the owner after the calibration.
6. **Calibration:** start it as a separate Run for 2 Generations to time a Generation and check disk use:
   `uv run 7wd-train start --run C:/7wd-runs/pantheon-calibration --config configs/pantheon-main.yaml --generations 2`
   (Pantheon games are longer than base ones: expect fewer Self-Play games per hour; resize `selfplay.games` so a
   Generation is ~1 desktop-hour, as for the main Run). Record the numbers and the agreed cap/plateau in these Notes.
7. **Start the Pantheon Run** (a new folder, never inside the repo):
   `uv run 7wd-train start --run C:/7wd-runs/pantheon --config configs/pantheon-main.yaml`
   This makes it the current Run, so the "Start training" / "Pause training" / "Training status" shortcuts then work
   on it as they did on the main Run.
8. **Success** (P-A8): the Pantheon best network passes the Pantheon Anchor (the "vs the Anchor" lower 95% bound
   above 50% in `generations.csv`).

Checked on the laptop (ticket 11): surgery from `laptop-gumbel/net/gen-0004.onnx` gives a checkpoint whose weights are
bit-identical to surgery from that Generation's `.pt`.

### Calibration and start (desktop, 2026-10-22)

- The main Run was **paused** (not stopped) at Generation 15 / 20.2 of 150 desktop-hours, best Generation 11. It
  resumes later with `7wd-train start --run C:/7wd-runs/main` (that makes it the current Run again).
- Surgery from `C:/7wd-runs/main/net/gen-0011.onnx` -> `C:/7wd-runs/pantheon-init.pt`: "same outputs as the parent in
  base-game positions".
- `training/configs/pantheon-main.yaml`: as step 5, with two corrections to step 5's list: `science_from/to` can't be
  null (the config check compares them); `science_min_share: null` alone turns that trigger off. `reference: null`.
- Calibration Run `C:/7wd-runs/pantheon-calibration` (2 Generations of 55,000 games):
  - Generation 0 (the surgery network, no Pantheon training yet) vs the Pantheon Anchor: **73.2%** (CI 68.7-77.4%),
    Elo 1666: it already passes the Anchor.
  - Self-Play ~68 min for 55,000 games (~13.4 games/s) plus ~15-19 min turning the games into a shard (dense
    temporary file ~30+ GB, compacted to 3.6 GB); 4.66M positions per 55,000 games (~85/game; base ~69).
  - Training ~255-260 s (~9,100 steps); each 400-game Match ~8-10 min.
  - A Generation took 6,051 s and 6,330 s; Generation 1 47.2% vs Generation 0, Generation 2 58.5% vs Generation 1
    and 60.8% vs Generation 0 (new best, Elo 1707).
  - Disk: 3.6 GB shard + 0.55 GB Game Records per 55,000 games; the 25M-position window is ~19 GB.
- `selfplay.games: 30000` sizes a Generation to ~1 desktop-hour.
- **Agreed with the owner:** `cap_hours: 20` (stop at ~20 desktop-hours like the main Run; extend with
  `start --ignore-stop` only if Generations are still clearly gaining). `plateau_generations: 20` kept (only matters
  if extended).
- The Pantheon Run `C:/7wd-runs/pantheon` started 2026-10-22 16:45; it is the current Run (outside the queue), so the
  Start/Pause/Status shortcuts act on it.

# 15: Pantheon training Run (Windows)

**What to build:** After the base main Run has stopped: a short calibration, then the Pantheon Run warm-started by surgery from the base "best" network, until a stop rule fires (P-A8).

**Blocked by:** 11, and the base main Run (v1 ticket 28) stopped

**Status:** ready-for-human (needs the Windows desktop)

Spec: `docs/spec-pantheon.md` (P-A8), on top of `docs/spec-v1.md`.

- [ ] Calibration numbers and the agreed cap/plateau in Notes
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

# 28: Main run (Windows)

**What to build:** The main Run until a stop rule fires.

**Blocked by:** 27

**Status:** in progress (the main Run is training on the Windows desktop since 2026-10-14)

Spec: `docs/spec-v1.md`.

- [ ] Best network passes the Anchor on a base-only Match

## Notes

### 2026-10-14: setup and start

- **Run:** `C:\7wd-runs\main` (outside the repo and OneDrive), config `training/configs/main.yaml`, the last entry of `runs/queue.yaml` (pilots 3 and 4 are marked `skip:` there; `7wd-train status` shows them as skipped). It is the current Run (`runs/current.txt`).
- **Config:** `desktop.yaml`'s Generation sizes and the pilots' winners: Small 256x4, Gumbel Self-Play at 64 sims (55,000 games, ~1 desktop-hour per Generation), derived features on, window 25M positions, reuse 2, Matches 200 pairs at 800 sims with classic search. Seed 1 (pilot 1 used 0, so the new Self-Play games don't repeat pilot 1's deals). **Cap 150 desktop-hours** (owner, 2026-10-14; the spec said 300). Plateau over 20 Generations; warm-start triggers kept (science under 1% over Generations 6-10, no progress by Generation 20) although the Run is already warm: if one fires it just stops and flags it.
- **Warm start (`init_from: C:/7wd-runs/pilot-1-small`):** creating the Run copies pilot 1's training checkpoint `net/latest.pt` (Generation 6's network, its Adam optimiser state and step count 44,042) into the main Run, so the main Run's Generation 0 *is* pilot 1's Generation 6 and training continues exactly where pilot 1 stopped (no learning lost). Until the main Run's own shards fill the 25M-position window (~Generation 7), its training window is topped up with pilot 1's newest shards (Generations 6, 5, 4, ... read in place, never deleted by the main Run). `run.json` records the lineage (`init_from`: parent, checkpoint hash and step, which Generation it holds, the parent's best and git commit). Checks at creation: the parent exists, isn't training, has a checkpoint, the same Encoding/manifest, network size and dropped inputs. Test: `test_main_run.py` checks on a tiny parent that the child's Generation 0 ONNX has exactly the parent's newest network's weights, training starts at the parent's step, and the parent's shards are only read.
- **Generation 0** (no Self-Play) plays the Anchor (`generation0_opponent: mcts`, so its Elo comes straight from the Anchor rather than Random) and the Reference, giving the first points of both curves.
- **The Reference (`matches.reference: mcts:sims=20000`):** pilot 1 already scores ~85% against the Anchor, where a score has little room left to show progress. The Reference is the Anchor's MCTS at 20x the simulations. Rated with `7wd match mcts:sims=20000 mcts` (200 seed pairs, 400 games, base-only, 824 s on 16 threads; `C:\7wd-runs\reference-rating`): **76.0% (CI 71.6-79.9%), +200 Elo (CI +160 to +240) → Elo 1712** on the Ladder (`engine/src/Agents/Ladder.cs`, `Ladder.Reference`). It wins mostly on civilian points (282 of 304 wins); no science wins either side. **678 ms per Move** (the Anchor: 32 ms), so a 200-pair Match against it would take ~11 min; the main Run plays **100 pairs** (`reference_pairs: 100`, ~5-6 min) with the Anchor Match every 5th Generation: ~1 min per desktop-hour on average. Caveat: pilot 1's Anchor Elo (~1815) is already above 1712, so the network should score ~60-65% against the Reference (not 50%); that still leaves far more headroom than the Anchor's 85%. If the network later saturates the Reference too (90%+), a stronger one (more sims, or a fixed strong network) is the next step.
- **Stopping rule with two fixed opponents:** the plateau and no-progress tests use whichever series (Anchor or Reference) the latest measurement scored *lower* against (normally the Reference), each compared only with itself (`stopping.headroom_series`). Plateau = that Elo hasn't risen by more than its error bar over the last 20 Generations (4 Matches). The 100-pair Reference Match has a wider error bar (~±50 Elo near 60%) than a 200-pair one (~±35).
- **Status and graphs:** `generations.csv` and TensorBoard get `ref_score/low/high/elo` (`elo/reference`, `match/vs_reference`); `7wd-train status` shows "reference NN% at Generation G = Elo E" next to the Anchor.
- **Advisor:** "Play against the AI" defaults to the main Run's best network (to pilot 1's best until the main Run has one). The running Advisor picks this up after a restart.
- **Fix found while testing:** Generation 0's Match folder was named after the opponent's Agent spec, which fails on Windows for specs with a colon (`mcts:sims=50`); Match folders now replace such characters, and Generation 0's Match against the Anchor is saved as `gen-0000-vs-anchor`. Also `pilot-small.yaml`'s cap now says 10 like the other pilots (pilot 1 itself was stopped at 12.7 h).
- **Tests:** C# Engine 342, Agents 98, Advisor 83; Python 74 (8 in `test_main_run.py`).

### 2026-10-14: slow after a shortcut restart (Windows power throttling), fixed

- **Symptom:** started from a shell, Generation 1's Self-Play ran at 1,507 positions/s and its Match took ~5 min. After the owner's pause (14:00) and restart with "Start training" (14:39), Self-Play ran at 316-331 positions/s (Generations 2-3) and the Generation 3 Match took ~51 min; the Self-Play `dotnet` used ~3 of 16 cores. Pilot 1's ~4 h Generation 5 was probably the same thing.
- **Cause:** Windows 11 power throttling (EcoQoS, "efficiency mode"). The loop and its 7wd children run windowless in the background, and their ProcessPowerThrottling ControlMask was 0, so Windows' background heuristic decided and throttled them. Priority (normal), affinity (all 16 CPUs) and the launch flags were not the cause: a benchmark launched with the same flags from a shell ran at full speed (1,516-1,538 positions/s).
- **Evidence:** calling `SetProcessInformation(ProcessPowerThrottling)` (opt out of execution-speed throttling) on the live, slow Self-Play process took it from 3.2 to 10.9 cores within seconds, with nothing else changed.
- **Fix:** the loop (`awake.full_speed()`, at startup, logged as "full speed: opted out of Windows power throttling") and the 7wd CLI (`PowerThrottling.OptOut()`, first thing in `Program.cs`) opt themselves out of execution-speed and timer-resolution throttling. Both do nothing off Windows. Tests: `test_pilots.py` (Python) and `PowerThrottlingTests` (C#) check the state on Windows.
- **After** (restarted with the desktop shortcut, 22:40): loop, Self-Play and Match processes show ControlMask 5 / StateMask 0 (opted out). Self-Play ran at a steady 24.5 games/s (~1,650 positions/s) with ~11.3 cores busy, and the Generation 4 Match took 4 min 43 s (vs ~51 min throttled). The session's average was 1,051 positions/s because it only had the last 11,687 games, where the slow drain at the end counts for more.
- **Tests:** C# Engine 342, Agents 99, Advisor 83; Python 75 (+1 skipped, the Linux no-op).

### How the owner pauses and resumes

- **Pause:** double-click **"Pause training"** on the desktop. The loop stops within ~30 s at a safe point (mid-Self-Play, mid-training or mid-Match all resume where they left off). Paused hours don't count toward the 150-hour cap. Pause before gaming or when the desktop is needed; Tabletop Simulator and the Advisor can run alongside training but everything will be slower.
- **Resume:** double-click **"Start training"**: it continues the current Run (`main`) from where it paused.
- **Check:** **"Training status"** (or `training\.venv\Scripts\7wd-train.exe status`) shows the state, Generations, Elo, the latest Anchor and Reference scores and hours used of the cap.
- When a stop rule fires (cap, plateau, or a warm-start trigger), the Run stops itself with a notification saying why; the owner decides what next (`start --ignore-stop` goes on past a cap/plateau).

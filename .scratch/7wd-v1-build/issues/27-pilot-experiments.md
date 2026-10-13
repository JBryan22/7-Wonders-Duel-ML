# 27: Pilot experiments (Windows)

**What to build:** The pilots from spec A7.

**Blocked by:** 26

**Status:** ready-for-human (needs the owner or the Windows desktop)

Spec: `docs/spec-v1.md`.

- [ ] Small vs Medium, derived features on/off, classic vs Gumbel compared on Anchor Elo
- [ ] Winners frozen into the main Run config

## Notes

- **Pilots and configs** (ticket 26's plan; each a sibling of `desktop.yaml` with `anchor_every: 2`, `cap_hours: 20`, plateau/warm-start rules off, same seed): 1 `pilot-small.yaml` (Small/Gumbel baseline, Run `C:\7wd-runs\pilot-1-small`, started 2026-10-12), 2 `pilot-medium.yaml` (512x6, Self-Play and Matches on CUDA, 40,000 games), 3 `pilot-classic.yaml` (classic Self-Play, `noise=0.25,alpha=0.3,temp=1`), 4 `pilot-no-derived.yaml` (`dropped_inputs: [derived]`). Compare on Anchor Elo at equal desktop-hours.
- **Pilot queue:** `runs/queue.yaml` (machine-local, next to `runs/current.txt`; `SEVENWD_QUEUE` overrides) lists the four Runs in order with their configs. "Start training" continues the current pilot; when it has finished (self-stop on its cap or a plateau, or done), the next Start moves on to the next unfinished pilot (creates its Run, makes it current, starts it). The loop does not move on by itself: the spec's control is always manual, and a finished pilot is the moment to look at it; the cap notification says "Start training moves on to the next pilot, X". A faulted pilot is not skipped; a current Run outside the queue is left alone; `start --run/--config/--ignore-stop` work as before. `7wd-train status` (and the "Training status" shortcut) prints one line per pilot, `>` marking the current one, with state, Generations, Elo, the latest Anchor score and Anchor Elo, and desktop-hours of the cap.
- **Sleep bug:** Windows went to sleep on idle at 21:04:35 on 2026-10-12 (event log: "System Idle") and woke at 12:07:09 the next day; the loop froze but `active_seconds` counted the 54,154 s of sleep (Python's `time.monotonic` on Windows is GetTickCount64, which runs through sleep). pilot-1's `status.json` was corrected to 11,143 s (3.1 h; old file in `status.json.before-sleep-fix`), consistent with the logs (65,297.7 - the logged sleep). Fixes:
  - `awake.py`: while the loop runs it holds `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` (no idle sleep; the display may still turn off), released on pause/stop/exit (Windows also drops it when the process dies). Linux: `systemd-inhibit --what=sleep:idle` when available. `SEVENWD_KEEP_AWAKE=0` switches it off. loop.log says "keeping the computer awake".
  - Desktop-hours (`runstate.ActiveClock`) now come from QueryUnbiasedInterruptTime on Windows (stops during sleep/hibernate; Linux `CLOCK_MONOTONIC` already does), and, as a second line of defence, a jump of more than 300 s between readings is not counted (the heartbeat reads every 15 s). The per-Generation `seconds` column uses the same clock.
- **No derived features:** config `dropped_inputs: [<input block>...]` (names from `data/encoding/v1.json`). The network multiplies those inputs by 0 before its first layer (a fixed mask inside the model, not saved in checkpoints), so Encoding v1, the C# encoder and the shards are unchanged and existing models/checkpoints work as before. Checked at Run creation/open (block names exist), recorded in `run.json`, the checkpoint (`network.dropped_inputs`), the ONNX metadata (`dropped_inputs`, only when non-empty) and the loop's export check; C# `NeuralNetwork` refuses a model naming an unknown block and shows "without derived". `sevenwd-train --drop-inputs derived` for manual use.
- Tests: Python 66 (15 new in `test_pilots.py`: sleep gap, the Windows clock, Generation seconds, keep-awake on Windows / off / held only while the loop runs, queue status, start moving on, faulted/outside-queue Runs, Anchor Elo in status, pilot configs, dropped-input config/network/checkpoint/export/ORT, a tiny no-derived Run through the C# `7wd`); C# Engine 342, Agents 98 (1 new: no-derived model + refused unknown block; new fixtures `no-derived.onnx`, `bad-drop.onnx`), Advisor 70.

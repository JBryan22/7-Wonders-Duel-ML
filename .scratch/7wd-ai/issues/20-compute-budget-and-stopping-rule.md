# Training compute budget and v1 stopping rule

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by:

## Question

How much training does v1 get, and how do we decide the network is "good enough" to call v1 done? We can't measure Engine or network speed before the code exists, so this ticket decides the **rules** for setting the numbers, not the numbers themselves:
- **Stopping rule:** e.g. a target Elo margin over the strongest non-neural rung (chance-node MCTS at a fixed budget), an Elo plateau over N Generations, a GPU-hours cap, or a combination.
- **Budget:** how many GPU-hours or days on the Windows desktop the owner will commit to a v1 run, and to the experiments already planned (Small vs Medium network, with vs without derived features, promo rate).
- **Tuning procedure:** once `7wd bench` and network throughput are measured, how the starting defaults (2,000 games per Generation, a ~1M-position window, 64 simulations) are revisited.
- **Advisor search budget:** how many simulations, or how many seconds, the Advisor uses per request on the desktop and on the laptop.
- **Heuristic warm start:** the trigger for switching it on, e.g. no science wins in Self-Play after N Generations.

## Context

- The algorithm and the loop are fixed: see "Agent Ladder and target algorithm" and "Training pipeline and two-machine workflow".
- Self-Play always includes both promo Wonders, and the headline Elo is measured on base-only Matches ("Promo Wonders in the Engine, training and Encoding").
- Speed target: `7wd bench` at least 10k random games/s ("Solution structure and Engine testing strategy").

## Answer

Resolved by grilling on 2026-10-09. New glossary terms: Anchor, Run.

This ticket fixes the **rules** for the numbers. The numbers themselves are set by the calibration run once the code exists.

**What "v1 is good enough" means.** There is a success bar and a separate condition for stopping.
- **Success:** the "best" network passes the Anchor (chance-node MCTS, 1,000 simulations) under the ladder's pass rule: in a 400-game Match, the lower 95% bound of its win rate is above 50%. The aim is ≥ +200 Elo over the Anchor (about 76% wins). It is reported against but does not block v1.
- **Stop:** whichever comes first:
  - **plateau:** Anchor Elo hasn't risen by more than its error bar over the last 20 Generations (4 Anchor Matches);
  - **cap:** the main Run has used 300 desktop-hours.
- **Cap reached before passing the Anchor:** don't ship anyway. Diagnose instead: warm start, network size, simulation count.

**Budget**, counted in **desktop-hours** (hours with the whole Windows desktop training). Desktop-hours are used because CPU-side Self-Play, not the GPU, is the likely bottleneck.
- Main Run: cap of 300 desktop-hours, roughly 3–4 weeks of nights and weekends.
- Experiments: 150 desktop-hours.
- Sanity check after `7wd bench`: 300 hours should buy roughly 500k+ Self-Play games. If it buys far fewer, raise it with the owner rather than quietly cutting quality.

**Experiments run first, as pilots** (sibling Runs, about 20 desktop-hours each, compared on Anchor Elo at equal desktop-hours):
1. Small (256×4) vs Medium (512×6) network.
2. With vs without derived features.
3. Classic vs Gumbel at an equal simulation count (a one-off sanity check).

The winners of 1 and 2 shape the main Run's config. Value-target mixing and playout cap randomisation run only if budget is left over; otherwise they move past v1. (The "promo rate" experiment is dropped: Self-Play always includes both promos.)

**Tuning procedure.** One short **calibration run** measures Self-Play positions per second and training steps per second. Then:
- **Generation length:** size each Generation to about 1 desktop-hour. Keep the training window at about 7 Generations, with each position seen about 2×.
- **Training simulations:** stay at 64, unless a 32 / 64 / 128 pilot at equal desktop-hours shows a clear winner.
- **Freeze:** the result goes into the main Run's frozen config and is not touched again.

**Advisor thinking budget.**
- 800 simulations on both machines.
- A "think longer" button runs 3,200.
- Wall-clock time is logged.
- If the laptop needs more than ~3 s at 800, its local default drops to 400. The Match standard is unchanged.

**Heuristic warm start.** Two triggers:
- science wins are under 1% of Self-Play games over Generations 6–10 (about 10k games);
- or there is no Anchor Elo progress by Generation 20.

Either trigger stops the Run (see below) and flags why. It never switches by itself. The owner decides whether to start the warm-start sibling Run.

**Starting and stopping training: always manual.**
- **Controls:**
  - two Windows desktop shortcuts, **Start training** and **Pause training**;
  - the same controls as `7wd train start | pause | status`, also usable from the laptop over SSH.
- **Start** always continues the current Run where it left off. There is no run to pick and no `--resume` to type.
- **Status** gives one line: what it is doing, how far it has got, the latest Elo, and why it stopped if it did.
- **Pause** frees the machine within about a minute and loses at most a minute of work:
  - Self-Play saves each finished game as it goes, and resuming continues from the last saved game;
  - training checkpoints every few minutes;
  - a Match in progress resumes from its last finished seed pair.

  This replaces "resume only at step boundaries" from "Training pipeline and two-machine workflow".
- **Nothing starts or pauses training automatically:** no Tabletop Simulator watcher and no schedule. Using the machine is the owner's call.
- **The one exception:** the Run stops itself when it no longer needs to run:
  - plateau;
  - cap;
  - a warm-start trigger;
  - a fault it can't recover from (it stops rather than retrying in a loop).

  It saves everything, records the reason in `status`, `generations.csv` and TensorBoard, and shows a Windows notification.

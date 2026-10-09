# Training pipeline and two-machine workflow

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 07, 08

## Question

How does the Self-Play → training → evaluation loop run end to end: which process generates games (C#) and in what format they're stored, how Python trains and exports ONNX, how new networks are gated/evaluated against the Agent Ladder, how experiments are tracked so the owner can see learning happen, and how the Linux (development) and Windows (GPU training) machines share code, data and models?

## Context from "Neural network input and output encoding"

Already decided there:
- Self-Play stores already-encoded examples plus Move Logs (ADR 0003).
- The heads and their target meanings are defined.

Still to settle here:
- loss weights for value, policy and outcome class;
- whether to mix the search's value estimate into the value target;
- the planned experiments: Small (256×4) vs Medium (512×6) network at equal GPU-hours, and with vs without derived features.

## Answer

Resolved by grilling on 2026-10-09. New glossary terms: Generation, Game Record.

**The loop runs in synchronous Generations.** Asynchronous Self-Play (KataGo-style) is a later optimisation that keeps the same file formats. Each Generation:
1. **Self-Play (C#):** `7wd selfplay` plays 2,000 games with the newest network and writes Game Records and encoded shards.
2. **Training (Python):** `train.py` trains on a sliding window of recent positions and exports ONNX.
3. **Measuring (C#):** `7wd match` plays the Generation's Matches.

**Orchestration.** A Python orchestrator, `loop.py`, calls the C# CLI as a subprocess. Each finished step writes a "done" marker into the run directory, so `loop.py --resume` continues after a crash or reboot. A shell script was rejected because Bash and PowerShell differ.

**Storage:**
- **Game Record** (kept forever, a few KB per game):
  - the Move Log;
  - for each searched decision, the sparse policy target (legal Moves' probabilities only);
  - the result;
  - the seeds for the Chance Source and the Gumbel noise, so any game can be replayed exactly.

  Policy targets can't be recomputed without rerunning the search, which is why they are stored.
- **Encoded shards** (a disposable cache): per-Generation NumPy `.npy` files that C# writes directly and Python memory-maps.
  - Contents: float16 inputs, sparse policies, value and outcome class.
  - Shards are deleted once their Generation leaves the training window.
  - They can be re-encoded from Game Records under a new Encoding version (ADR 0003).
  - Storing everything densely would be about 650 GB per million games.

**No gating: Self-Play always uses the newest network** (AlphaZero style; AlphaGo Zero's 55% gate was rejected). Matches measure but don't gate:
- every Generation: 400 games against the previous Generation;
- every 5 Generations: against the chance-node MCTS anchor, which keeps Elo on the shared ladder scale (Random = 0).

A separate **"best" pointer**, the network the Advisor uses, moves only when a Generation beats the current best by the ladder's pass rule (lower 95% bound > 50%).

**Self-Play details:**
- The played Move is the one Gumbel search selects. Its Gumbel noise provides the exploration, so there is no temperature or Dirichlet noise. Those remain only in classic debug mode.
- Only searched decisions become training examples. Single-option decisions are skipped.
- Generation 0 is a randomly initialised network: pure Self-Play first, with the warm start in reserve per "Agent Ladder and target algorithm".

**Loss:**
- policy cross-entropy (weight 1) against the improved policy;
- value MSE (weight 1) against the pure game result *z*;
- outcome-class cross-entropy (weight 0.25);
- AdamW weight decay 1e-4.

There is no search-value mixing in v1.

**Starting defaults** (all in the run config, to be tuned once speed is measured):

| Setting | Default |
|---|---|
| Games per Generation | 2,000 (~150k positions) |
| Training window | newest ~1M positions (~7 Generations) |
| Training steps | each new position seen ~2× on average; batch 1,024 (~300 steps) |
| Optimiser | AdamW, learning rate 1e-3, constant |
| Matches | vs previous Generation every Generation (400 games); vs the anchor every 5 |

**Runs and reproducibility:**
- A run is a directory `runs/<name>/`. It is started from a frozen `config.yaml` that holds the network size, simulations, the numbers above, the Encoding version and seeds.
- At start, the run directory records the git commit, the Encoding manifest and the machine.
- Changing a running run's config is refused. Experiments are sibling runs, optionally initialised from another run's network, so every Elo curve traces to exactly one setup.

**Watching learning:**
- TensorBoard: loss curves, Elo per Generation, victory-type mix, policy entropy.
- A plain `generations.csv`: one row per Generation, so nothing depends on the tool.
- Weights & Biases is a possible later swap.

**Two machines:**
- Code goes through git (GitHub). Run directories, data and networks stay out of git.
- The Windows desktop (NVIDIA GPU) owns real runs.
- The Linux laptop runs the same pipeline in a "tiny" CPU config (e.g. 10-game Generations, 8 simulations) for development and smoke tests.
- Remote control goes over SSH to Windows via Tailscale, and TensorBoard through an SSH tunnel.
- Networks are copied to the laptop with `scp` when needed. The Advisor and TTS run on Windows anyway. Syncthing or Git LFS are fallbacks if copying gets tedious.

**Planned experiments** (each a sibling run):
- Small (256×4) vs Medium (512×6) network at equal GPU-hours;
- with vs without derived features;
- value-target mixing;
- playout cap randomisation;
- classic vs Gumbel at an equal simulation count (from "Agent Ladder and target algorithm").

**Handed on:**
- The real numbers, the stopping rule and the warm-start trigger stay in the map's "Compute budget and stopping rule" note.
- The Python environment (uv, CPU vs CUDA PyTorch on each machine) goes to "Solution structure and Engine testing strategy".

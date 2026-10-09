# Solution structure and Engine testing strategy

Type: grilling
Status: open
Blocked by: 06

## Question

How is the repo laid out (C# solution/projects, Python package, shared encoding spec, docs), and how do we prove the Engine follows the rules — rulebook-example tests, scripted full-game replays, random-play invariants (coins never negative, game always terminates), cross-checks against a prior-art engine — before any learning relies on it?

## Context from "Training pipeline and two-machine workflow"

The layout must hold:
- the C# CLI (`7wd selfplay`, `7wd match`);
- Python `train.py` and the `loop.py` orchestrator;
- `runs/<name>/` directories, which are git-ignored.

Also to settle here: the Python environment on both machines (e.g. uv, with CPU PyTorch on the laptop and CUDA PyTorch on Windows).

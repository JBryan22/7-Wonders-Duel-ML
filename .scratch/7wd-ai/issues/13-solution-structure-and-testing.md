# Solution structure and Engine testing strategy

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 06

## Question

How is the repo laid out (C# solution/projects, Python package, shared encoding spec, docs), and how do we prove the Engine follows the rules — rulebook-example tests, scripted full-game replays, random-play invariants (coins never negative, game always terminates), cross-checks against a prior-art engine — before any learning relies on it?

## Context from "Training pipeline and two-machine workflow"

The layout must hold:
- the C# CLI (`7wd selfplay`, `7wd match`);
- Python `train.py` and the `loop.py` orchestrator;
- `runs/<name>/` directories, which are git-ignored.

Also to settle here: the Python environment on both machines (e.g. uv, with CPU PyTorch on the laptop and CUDA PyTorch on Windows).

## Answer

Resolved by grilling on 2026-10-09. No new glossary terms and no ADR: these are engineering choices that are cheap to change.

**Repo layout:**

| Folder | Holds |
|---|---|
| `engine/` | C# solution `SevenWondersDuel.sln` (.NET 10), all C# projects and tests |
| `training/` | Python package: `train.py`, `loop.py`, `pyproject.toml`, `uv.lock`, pytest tests |
| `data/` | Shared data: card JSON (`data/base/`) and the committed Encoding manifest (`data/encoding/v1.json`) |
| `docs/` | Spec, ADRs, research (`docs/research/`, now on main) |
| `tts/` | Placeholder for the mod's Lua scripts; contents are for "Tabletop Simulator Adapter architecture" |
| `runs/` | Git-ignored run directories |

**C# projects:**
- `Engine`: Game State, Moves, rules, Move Log, Observation, and the Encoding in its own folder. No outside dependencies.
- `Agents`: Random, Heuristic, PIMC, chance-node MCTS, Gumbel AlphaZero, ONNX inference.
- `Cli`: the `7wd` program (`selfplay`, `match`, `replay`, `bench`, …).
- `Advisor`: the local web server for the Advisor Window; Adapters plug in here. The page is plain HTML/JS modules with no build step and no Node.js.
- `Engine.Tests`, `Agents.Tests`: xUnit, plus a property-testing library for the random-play invariants.

**Card data:** a JSON file in `data/base/` (73 cards, 12 Wonders, 10 tokens), starting from the cross-checked set in "Base game rules reference", with attribution. Loaded once at startup into flat integer arrays. Pantheon/Agora would arrive as new files.

**Proving the Engine follows the rules:**
1. **Rule tests:** one small test per rule, from the rulebook's examples and the 20 common mistakes in `docs/research/rules-reference.md`.
2. **Scripted games:** Move Logs with expected scores and winner that were worked out *independently* of our Engine:
   - hand-traced short scenarios for targeted rules;
   - about 10 full games frozen after the cross-check agrees, covering military, science and civilian wins and a draw;
   - later, real games from the table whose scores match the score sheet.
3. **Random-play invariants** over thousands of games:
   - coins are never negative;
   - every game ends;
   - there is a legal Move unless the game is over;
   - replaying the Move Log gives an identical Game State;
   - a copied state never affects the original;
   - the Observation never holds the opponent's Great Library draw.
4. **Data tests:** card counts, chain links resolve, the science symbol distribution, the Age layout shapes.
5. **Cross-check against indjev99's C++ engine (MIT):** play the same Moves and Reveals through both engines and flag any disagreement on legal Moves or final scores. Runs on Linux only and is opt-in, not part of CI. It is run once before training first trusts the Engine, then again after rules changes. Its start-player rule is matched via our flag.

**Other guards:**
- **Encoding manifest is committed:** a test fails if the generated manifest differs from `data/encoding/v1.json`, so any Encoding change, even an accidental one, is a visible diff that forces either a revert or a version bump.
- **Determinism:** the Engine and Chance Source use our own seeded random number generator, so the same seed gives the same Reveals and Game State on Linux and Windows, and CI tests this. Full search with a network is reproducible only on the same machine and runtime, not across machines. Replaying a Move Log never needs that.
- **Training-loop smoke test:** `loop.py` runs 2 Generations in the tiny config (Self-Play → training → ONNX export → Match) and checks the files and the Encoding-version handshake. About a minute on CPU. Run in CI on Linux and before any real run on Windows.

**CI:** GitHub Actions runs the fast C# and Python tests on Linux and Windows on every push. The cross-check and long random-play soaks stay out.

**Speed:** `7wd bench` reports random games/s and Self-Play positions/s; you run it by hand and the results are written down. The target is at least 10k random games/s on one laptop core, about 1/6 of indjev99's C++ engine. Missing it triggers an optimisation pass before training. The measured numbers feed the "Compute budget and stopping rule" note.

**Python environment:** uv, Python ≥ 3.12, one `pyproject.toml` and a committed lock file. PyTorch's CPU build is selected on Linux and its CUDA build on Windows by platform markers, so `uv sync` is the only setup step on either machine.

**Housekeeping done:**
- The four research documents were merged into `docs/research/` on main, and the research tickets now point there.
- The leftover agent worktrees and `worktree-agent-*` branches were deleted.
- The `research/*` branches are kept (they are also on GitHub).

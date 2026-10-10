# 23: The training loop (tiny version)

**What to build:** loop.py Generations, sliding window, shard cleanup, Matches, the best pointer, Run directories with frozen config, TensorBoard and generations.csv; the tiny config smoke test.

**Blocked by:** 22

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] 2-Generation smoke test passes in CI
- [x] An edited config refuses to start

## Notes

- Code: `training/sevenwd/loop.py` (+ `training/loop.py` stub, script `sevenwd-loop`), `sevenwd/config.py` (`RunConfig` with sections `network`, `selfplay`, `training`, `matches`; unknown keys refused), `training/configs/default.yaml` (the spec's starting defaults, every key listed; a test checks it equals the built-in defaults) and `configs/tiny.yaml`. New deps: `pyyaml`, `tensorboard`. `train.py` history now also has `entropy` (policy entropy over legal Moves, nats).
- Usage: `uv run sevenwd-loop <run dir> --config configs/x.yaml` creates a Run; `uv run sevenwd-loop <run dir> [--generations N]` continues it. Exit 0 done, 1 refused/failed, 2 stopped (a C# step exited 2 or Ctrl+C). The loop builds `engine/src/Cli` (Release) once per start and runs `dotnet 7wd.dll` with cwd = the Run dir and Run-relative paths (`--cli` / `SEVENWD_CLI` overrides). C# output goes to `log.txt` in each step's directory; the loop's own lines to `loop.log`.
- **Run layout** (documented in `loop.py`'s docstring): `config.yaml` (frozen copy), `run.json` (config sha256 of the file bytes, Encoding version, manifest hash of the parsed JSON so CRLF/LF checkouts agree, git commit + dirty flag, machine incl. GPU), `manifest.json`, `sessions.jsonl` (commit/machine per start), `generations.csv`, `best.json`, `tb/`, `net/latest.pt` + `net/gen-NNNN.onnx` + `net/gen-NNNN.train.json` (plan: start step, steps, window; then losses, seconds), `selfplay/gen-NNNN/`, `matches/gen-NNNN-vs-<name>/`.
- **Refusals:** frozen config edited (byte hash) → refuses, message says put it back or start a sibling Run; `--config` differing from the frozen one; Encoding version ≠ config's/run's; manifest content changed. A config with `config.yaml` present = a complete Run (copied last at creation).
- **Generations:** Gen 0 = random net (or `init` checkpoint) exported, then a Match vs `generation0_opponent` (Random) to start the Elo chain on the shared scale. Gen N ≥ 1: Self-Play with gen N−1 (seed `seed + N·10⁷`) → train on the window → export (Python checks the ONNX's `encoding_version` before renaming into place) → Match vs gen N−1 (`--elo-b` = its Elo), vs the best when best ≠ N−1, vs `matches.anchor` when N % `anchor_every` == 0 → best pointer moves when the Match vs best passes (lower 95% bound > 50%) → delete shards older than the window. Elo column = chain (prev Elo + Elo diff); `anchor_elo` from the Anchor Match when the anchor is rated (Ladder or `anchor_elo`). Match seeds are the same every Generation (`--seed = seed`).
- **Window:** newest Generations, adding one while the newer ones hold fewer than `window_positions` (so it covers ≥ the window, overshooting by up to one Generation). Training steps = max(`min_steps`, ⌈`reuse` × new positions / batch⌉); one checkpoint carries network + optimiser across Generations; training seed varies per Generation.
- **Resume:** every step skips finished work (shard present; ONNX present; Match summary complete); C# resumes Self-Play/Matches itself; training resumes from the plan + checkpoint step (`remaining = start + steps − checkpoint step`). A row is appended to `generations.csv` (fsync) only when a Generation is done. Pause semantics/self-stop/`7wd train` are ticket 24; Ctrl+C currently kills the step (finished games/pairs kept).
- **TensorBoard** per Generation: loss/*, policy/entropy, elo/generation, elo/anchor, match/vs_previous, match/vs_anchor, best/generation, selfplay/victory_* (shares; draws separate), selfplay/positions_per_second, training/steps_per_second, training/window_positions, time/generation_seconds. `uv run tensorboard --logdir <run>/tb`.
- **Tiny config:** 10-game Generations, 8 sims, 64×2 net, batch 256, window 1,000 (~2 Generations of ~700 positions), Matches 5 pairs at 8 sims, anchor `mcts:sims=50` every 2 Generations (unrated, just exercises the path). 2 Generations take ~17 s on the desktop (incl. ~3 s C# build); the test module ~16 s.
- Note for ticket 24/26: when the best lags behind N−1, every Generation plays an extra 400-game Match vs the best; at real sizes that's a notable cost (consider fewer pairs for that Match if it matters).
- Windows/OneDrive: `uv sync` fails to hardlink from the cache into the OneDrive folder; `UV_LINK_MODE=copy` works. A harmless "failed to remove sevenwd dist-info" error appears on re-sync.
- Tests: Python 32 (8 new in `test_loop.py`: default config = built-in defaults, unknown/bad settings refused, run records its setup, edited/different config refused (and accepted again once restored), new-directory/Encoding refusals, window + shard cleanup, the 2-Generation smoke test through real `7wd`, rerun redoes nothing and an edited copy of the smoke Run refuses). C# Engine 342, Agents 92 (unchanged). Determinism hashes and frozen C++ games untouched.

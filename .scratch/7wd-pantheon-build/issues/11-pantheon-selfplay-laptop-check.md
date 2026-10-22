# 11: Pantheon Self-Play and the laptop learning check

**What to build:** `expansion: pantheon` in Run configs, Self-Play and shards with v2, the loop's Matches against the Pantheon Anchor, a tiny Pantheon config and a short laptop Run warm-started by surgery from the current base "best" network.

**Blocked by:** 10

**Status:** done

Spec: `docs/spec-pantheon.md` (P-A8), on top of `docs/spec-v1.md`.

- [x] The tiny-loop smoke test passes with Pantheon in CI
- [x] A short laptop Run shows Pantheon Elo against Random rising (numbers in Notes)
- [x] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes
- **Run config:** `expansion: null | pantheon` (`RunConfig.expansion`, `config.EXPANSIONS`; pantheon needs `encoding_version: 2`). `default.yaml` lists it as `null`; base Runs unchanged (a missing key = null; frozen configs and run.json untouched). The loop picks the manifest by the config's Encoding (`default_manifest_path(config.encoding_version)`), passes `--expansion pantheon` to `7wd selfplay` and every `7wd match` (and `sevenwd.curve`), so the Elo column and Anchor are on the Pantheon scale. `check_init` refuses at creation an `init` checkpoint of another Encoding and names the surgery command.
- **C#:** `SelfPlaySettings.Expansions`; the session's Encoding is the network's (`AgentSource.EncodingFor`, opened before any game, so a v1 network + Pantheon is refused with nothing written). `SelfPlayHeader.Expansion` (`"pantheon"`, omitted for base: base headers byte-identical) and `EncodingVersion`; shards written in that Encoding. `7wd selfplay --expansion E`; `7wd shard ... [--version V]` (default: the header's Encoding).
- **Surgery from ONNX** (for ticket 15): `sevenwd-train surgery <run>/net/gen-NNNN.onnx --out X.pt` (`surgery.load_onnx_network`): Runs keep only `net/latest.pt` but every Generation's ONNX, whose initializers are the state_dict under the same names. Checked: surgery from `laptop-gumbel/net/gen-0004.onnx` is bit-identical to surgery from its `.pt`. No change to the loop's checkpoint handling (an earlier draft kept a `gen-NNNN.pt` copy of the best; dropped, since it changed what a restarted base Run does and ONNX already covers every Generation). Steps for the desktop are in ticket 15's Notes.
- **Configs:** `tiny-pantheon.yaml` (CI smoke: 2 Generations, surgery from the tiny base Run), `laptop-pantheon.yaml` (laptop-gumbel sizes, 8 Generations, anchor heuristic).
- **Laptop Run** `~/7wd-runs/laptop-pantheon-final` (outside the repo), parent: `~/7wd-runs/laptop-gumbel` (a laptop-gumbel.yaml base Run, Encoding v1, 128x2) Generation 4 = its best, via surgery from `gen-0004.onnx` (`~/7wd-runs/pantheon-init-gen4-onnx.pt`). ~25 min of loop compute for 8 Generations (400 games each) + ~15 min for the two curves. Results identical to an earlier run of the same setup from the `.pt` (`~/7wd-runs/laptop-pantheon`).
  - vs Random (Pantheon, 200 games each), Gen 0-8: 97.5%, 98.5%, 99.5%, then 100% from Gen 3 (Elo 636 -> 727 -> 920 -> 1040, the cap for 200/200). The surgery start already beats Random; the curve script calls it "does NOT rise" only because it saturates.
  - vs the Heuristic (Pantheon Elo 1250, 200 games each), Gen 0-8: 2.5%, 10.5%, 8.5%, 13.5%, 14.0%, 13.5%, 23.5%, 22.0%, 25.0% (Elo 614 -> 1059, +44/Generation; "RISES"). In-loop Anchor (heuristic) Matches: Gen 4 12%, Gen 8 30%; Elo chain 676 -> 992.
- **Tests:** Engine 472, Agents 126 (+3 Pantheon Self-Play), Advisor 83; Python 87 passed / 3 skipped (+ Pantheon config checks, Pantheon Run/init refusal, 2-Generation Pantheon smoke, surgery from ONNX). Base 100k soak 96,502 (152) / 3,395 / 103 and messe,liberty 96,140 (135) / 3,715 / 145, unchanged; determinism hashes and FrozenGames green.

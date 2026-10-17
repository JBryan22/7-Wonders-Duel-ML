# 10: Network surgery and Python for Encoding v2

**What to build:** Python reads the v2 manifest; `sevenwd-train surgery` makes a v2 network from a v1 checkpoint (P-A7); ONNX export stamped v2; C# loads it.

**Blocked by:** 08, 09

**Status:** done

Spec: `docs/spec-pantheon.md` (P-A7), on top of `docs/spec-v1.md`.

- [x] Exact check: in base-game positions the operated network's value, outcome and v1 policy outputs equal its parent's
- [x] Overfit test passes on Pantheon positions
- [x] `7wd match neural:<v2 onnx> random --expansion pantheon` runs
- [x] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

- **Built:** `training/sevenwd/surgery.py`: `operate(parent, v1_manifest, v2_manifest)` (copies trunk, final norm, value/outcome heads; stem v1 columns and policy v1 rows copied, new columns/rows and bias 0; keeps width/blocks/`dropped_inputs`), `check_extends` (v2 names must start with v1's, same outcome classes), `check_same_in_base_games` (run by `operate`: random base-shaped inputs, Pantheon inputs 0, value/outcome/v1 policy must agree, rtol 1e-4 / atol 1e-5; raises `SurgeryError`), `surgery(...)` writes a checkpoint (step 0, fresh AdamW, `"surgery": {parent, parent_step, parent_encoding_version}`; `save_checkpoint(..., extra=)`). CLI: `uv run sevenwd-train surgery PARENT.pt --out CHILD.pt [--onnx CHILD.onnx] [--manifest v2.json] [--parent-manifest v1.json]` (dispatched from `train.main`; the plain `sevenwd-train` CLI is unchanged). Refuses overwriting the parent and a parent that isn't v1 (exit 1).
- **Not bit-exact:** outputs agree to float32 rounding (~2e-6 on 256x4; BLAS sums the wider product in another order). Spec P-A7 now says so. Parents must be checkpoints: dynamo ONNX export de-duplicates equal initializers (e.g. `final_norm` folded into `trunk.0.norm` on a fresh network), so ONNX can't be read back reliably.
- **C# check through ONNX:** fixtures `surgery-parent.onnx`/`surgery-child.onnx` and `-no-derived` pair (8x1, perturbed weights; `write_surgery_test_models` in `sevenwd.test_models`; v1 fixtures and zero-v2 not regenerated). `Agents.Tests/SurgeryTests.cs`: on ~1,000 real base(+promo) positions encoded v1 vs v2, value/outcome/v1 policy equal the parent's, new logits 0; the child plays a Pantheon Match; v1 parent refused for Pantheon. Real CLI run: v1 checkpoint -> surgery -> `7wd match neural:pantheon.onnx,sims=32 random --expansion pantheon --pairs 3` completes; the v1 parent is refused.
- **Overfit on Pantheon positions:** `test_overfit_on_pantheon_positions_from_an_operated_network` (v2 shard, Pantheon inputs set, targets on new policy rows, init from a surgery checkpoint) reaches total loss < 0.05 in 400 steps. Training with `--manifest data/encoding/v2.json` refuses v1 shards.
- **Tests:** Python 85 passed / 3 skipped (new `test_surgery.py` 9, + 1 fixture test); Engine 472, Agents 123 (+3), Advisor 83 (one Advisor test failed once while the Python suite ran in parallel; passes alone). Base 100k soak 96,502 (152) / 3,395 / 103, unchanged; determinism hashes and FrozenGames green; no Engine code changed.
- **For ticket 11:** the loop still loads `load_manifest()` (v1) in `create_run`/`open_run`; a v2 Run needs `default_manifest_path(config.encoding_version)`. Start the Pantheon Run with config `init: <surgery checkpoint>` (weights only, fresh optimiser) and `encoding_version: 2`; `init_from` won't work across Encodings (it copies the parent's checkpoint and checks the same manifest). The base "best" network exists only as `gen-N.onnx`; the only checkpoint is `net/latest.pt` (the newest Generation): operate on that (copied out of the Run first, never write in a Run dir), or decide with the owner.


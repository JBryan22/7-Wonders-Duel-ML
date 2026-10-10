# 19: Python network, training and export

**What to build:** Residual MLP built from the manifest, the losses, train.py on shards, ONNX export stamped with the Encoding version.

**Blocked by:** 18

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Overfit test drives loss near zero
- [x] Exported ONNX carries the Encoding version

## Notes

- Code (`training/sevenwd/`): `manifest.py` (loads `data/encoding/v1.json`, checks format, sizes, names, blocks tile), `network.py` (pre-norm residual MLP: Linear stem → blocks of `x + fc2(relu(fc1(relu(LayerNorm(x)))))` → LayerNorm+ReLU → three heads; 256×4 = 1.54M parameters), `losses.py` (masked policy CE ×1, value MSE ×1, outcome CE ×0.25), `shards.py`, `export.py`, `train.py`. Run with `uv run sevenwd-train …` (or `uv run python train.py …` from `training/`). AdamW lr 1e-3, wd 1e-4, batch 1024, float32 (no AMP yet).
- **ONNX contract for ticket 20:** opset 18, one self-contained file. Input `input` float32 `[batch, 2417]` (dynamic batch). Outputs `policy` float32 `[batch, 1509]` **raw, unmasked logits** (C# masks illegal Moves to −∞ and softmaxes), `value` float32 `[batch]` (tanh, already in −1..1), `outcome` float32 `[batch, 7]` raw logits. Metadata (`ModelMetadata.CustomMetadataMap`): `format=7wd-network`, `encoding_version=1`, `input_size`, `policy_size`, `outcome_size`, `outcome_classes` (comma list, manifest order), `width`, `blocks`. Verified with onnxruntime 1.31 against torch outputs.
- **Shard format for ticket 22** (C# writes, Python memory-maps; full spec in `shards.py` docstring): a directory with `shard.json` `{"format":"7wd-shard","encoding_version":1,"positions":N}` and `input.npy` float16 `[N,2417]`, CSR sparse policies `policy_offset.npy` int64 `[N+1]`, `policy_index.npy` int16 `[M]`, `policy_prob.npy` float32 `[M]`, `value.npy` float32 `[N]`, `outcome.npy` int8 `[N]`. Every legal Move is listed per row (also with target 0): that list is the legal mask. `--data` takes shard dirs or folders searched recursively; mismatched Encoding versions are refused.
- `train.py`: `--steps N` takes N steps now; `--checkpoint` resumes (network, optimiser, cumulative `step`) and is saved atomically at the end and every `--checkpoint-minutes` (5); `--init` warm-starts from another checkpoint; `--steps 0` without data exports a random Generation-0 network. Checkpoints refuse a different Encoding version. Pause/resume mid-call semantics are left to ticket 24.
- PyTorch 2.14.1 via uv platform markers: CPU wheels on Linux, CUDA 13.0 (`cu130`) on Windows (works on CPU too, e.g. the CI runner). Export uses the dynamo exporter (needs `onnxscript`). onnxruntime is a dev dependency (tests only). uv 0.13 was installed on the desktop with the official installer (`~/.local/bin`).
- Tests: 21 Python (overfit reaches total loss ≈ 0.0025 from ≈ 3.3 in 400 steps on CPU and CUDA; threshold 0.05), C# 342 Engine + 65 Agents unchanged.

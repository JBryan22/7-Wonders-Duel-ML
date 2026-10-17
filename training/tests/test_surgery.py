"""Network surgery (spec-pantheon P-A7, ticket 10): a v1 network becomes a v2 (Pantheon) network that gives the same
outputs in base-game positions, and Encoding v2 networks train."""
import dataclasses

import numpy as np
import onnxruntime as ort
import pytest
import torch

from sevenwd.export import INPUT_NAME, read_metadata
from sevenwd.manifest import default_manifest_path, load_manifest
from sevenwd.network import Network, NetworkConfig
from sevenwd.shards import write_shard
from sevenwd.surgery import SurgeryError, check_same_in_base_games, operate
from sevenwd.train import TrainConfig, load_checkpoint, main, train

from conftest import synthetic_positions


@pytest.fixture(scope="module")
def v2():
    return load_manifest(default_manifest_path(2))


def trained_like(manifest, dropped=(), seed=1, width=64, blocks=2):
    """A v1 network with non-trivial weights everywhere (fresh init has LayerNorm 1/0 and would hide copy bugs)."""
    torch.manual_seed(seed)
    net = Network(NetworkConfig.from_manifest(manifest, width, blocks, dropped))
    with torch.no_grad():
        for p in net.parameters():
            p.add_(torch.randn_like(p) * 0.1)
    return net.eval()


@pytest.mark.parametrize("dropped", [(), ("derived",)])
def test_surgery_copies_the_trunk_and_heads_and_zeroes_the_new_parts(manifest, v2, dropped):
    parent = trained_like(manifest, dropped)
    before = {k: v.clone() for k, v in parent.state_dict().items()}
    child = operate(parent, manifest, v2)
    n_in, n_pol = manifest.input_size, manifest.policy_size

    c = child.config
    assert (c.input_size, c.policy_size, c.outcome_size) == (v2.input_size, v2.policy_size, v2.outcome_size)
    assert (c.width, c.blocks, c.dropped_inputs, c.dropped_ranges) == (
        parent.config.width, parent.config.blocks, parent.config.dropped_inputs, parent.config.dropped_ranges)
    ps, cs = parent.state_dict(), child.state_dict()
    assert set(ps) == set(cs)
    for key in ps:
        if key == "stem.weight":
            assert torch.equal(cs[key][:, :n_in], ps[key]) and not cs[key][:, n_in:].any()
        elif key.startswith("policy_head."):
            assert torch.equal(cs[key][:n_pol], ps[key]) and not cs[key][n_pol:].any()
        else:
            assert torch.equal(cs[key], ps[key]), key
    # The parent is untouched.
    assert all(torch.equal(before[k], v) for k, v in parent.state_dict().items())


@pytest.mark.parametrize("dropped", [(), ("derived",)])
def test_in_base_game_positions_the_operated_network_answers_like_its_parent(manifest, v2, dropped):
    """The exact check: base positions have every Pantheon input 0; value, outcome and the v1 policy logits agree."""
    parent = trained_like(manifest, dropped, width=256, blocks=4)
    child = operate(parent, manifest, v2)
    inputs = torch.from_numpy(synthetic_positions(manifest, 128, seed=5)[0].astype(np.float32))
    wide = torch.cat([inputs, torch.zeros(len(inputs), v2.input_size - manifest.input_size)], dim=1)
    with torch.no_grad():
        p_policy, p_value, p_outcome = parent(inputs)
        c_policy, c_value, c_outcome = child(wide)
    assert torch.allclose(c_policy[:, :manifest.policy_size], p_policy, rtol=1e-4, atol=1e-5)
    assert torch.allclose(c_value, p_value, rtol=1e-4, atol=1e-5)
    assert torch.allclose(c_outcome, p_outcome, rtol=1e-4, atol=1e-5)
    assert not c_policy[:, manifest.policy_size:].any()  # the new Moves start uniform (masked in base games anyway)
    assert check_same_in_base_games(parent, child) < 1e-5


def test_the_check_catches_a_wrong_copy(manifest, v2):
    parent = trained_like(manifest)
    child = operate(parent, manifest, v2)
    with torch.no_grad():
        child.stem.weight[:, :manifest.input_size] = torch.roll(child.stem.weight[:, :manifest.input_size], 1, dims=1)
    with pytest.raises(SurgeryError, match="differs from its parent"):
        check_same_in_base_games(parent, child)


def test_surgery_needs_an_encoding_that_extends_the_parents(manifest, v2):
    parent = trained_like(manifest)
    with pytest.raises(SurgeryError, match="doesn't extend"):
        operate(parent, manifest, manifest)
    renamed = dataclasses.replace(v2, input_names=("x",) + v2.input_names[1:])
    with pytest.raises(SurgeryError, match="first 2417 inputs differ"):
        operate(parent, manifest, renamed)
    with pytest.raises(SurgeryError, match="sizes"):
        operate(trained_like(manifest), v2, dataclasses.replace(v2, input_size=v2.input_size + 1,
                                                                 input_names=v2.input_names + ("y",)))


def test_cli_operates_on_a_checkpoint_and_exports_an_encoding_v2_onnx(manifest, v2, make_shard, tmp_path):
    parent_ckpt, parent_onnx = tmp_path / "base.pt", tmp_path / "base.onnx"
    small = ["--width", "16", "--blocks", "1", "--device", "cpu", "--log-every", "0", "--drop-inputs", "derived"]
    make_shard("shards/gen-0", n=16)
    assert main(["--data", str(tmp_path / "shards"), "--steps", "3", "--batch-size", "8", "--out", str(parent_onnx),
                 "--checkpoint", str(parent_ckpt), *small]) == 0

    child_ckpt, child_onnx = tmp_path / "pantheon.pt", tmp_path / "pantheon.onnx"
    assert main(["surgery", str(parent_ckpt), "--out", str(child_ckpt), "--onnx", str(child_onnx)]) == 0
    saved = load_checkpoint(child_ckpt, v2)
    assert saved["step"] == 0 and saved["surgery"]["parent_step"] == 3
    assert saved["surgery"]["parent_encoding_version"] == 1
    assert tuple(saved["network"]["dropped_inputs"]) == ("derived",)
    meta = read_metadata(child_onnx)
    assert (meta["encoding_version"], meta["input_size"], meta["policy_size"]) == ("2", "3206", "3420")
    assert meta["dropped_inputs"] == "derived" and meta["width"] == "16"

    # Through ONNX Runtime too: base-shaped inputs give the parent's outputs.
    x = synthetic_positions(manifest, 32, seed=9)[0].astype(np.float32)
    wide = np.concatenate([x, np.zeros((len(x), v2.input_size - manifest.input_size), np.float32)], axis=1)
    run = lambda path, inp: ort.InferenceSession(str(path), providers=["CPUExecutionProvider"]).run(None, {INPUT_NAME: inp})
    (pp, pv, po), (cp, cv, co) = run(parent_onnx, x), run(child_onnx, wide)
    np.testing.assert_allclose(cp[:, :manifest.policy_size], pp, rtol=1e-4, atol=1e-5)
    np.testing.assert_allclose(cv, pv, rtol=1e-4, atol=1e-5)
    np.testing.assert_allclose(co, po, rtol=1e-4, atol=1e-5)

    # Refused: overwriting the parent, and operating on a network that is already v2.
    assert main(["surgery", str(parent_ckpt), "--out", str(parent_ckpt)]) == 1
    assert main(["surgery", str(child_ckpt), "--out", str(tmp_path / "again.pt")]) == 1
    assert not (tmp_path / "again.pt").exists()
    # The v1 checkpoint still trains as before.
    assert main(["--data", str(tmp_path / "shards"), "--steps", "1", "--batch-size", "8", "--out", str(parent_onnx),
                 "--checkpoint", str(parent_ckpt), *small]) == 0


def pantheon_positions(manifest, v2, n, seed):
    """Synthetic Pantheon positions: Pantheon inputs set, and the legal Moves mostly Pantheon ones (indices >= v1's)."""
    rng = np.random.default_rng(seed)
    inputs = (rng.random((n, v2.input_size)) < 0.1).astype(np.float16)
    policies = []
    for _ in range(n):
        pantheon = rng.choice(np.arange(manifest.policy_size, v2.policy_size), size=rng.integers(2, 10), replace=False)
        base = rng.choice(manifest.policy_size, size=rng.integers(0, 3), replace=False)
        legal = np.concatenate([pantheon, base])
        prob = np.zeros(len(legal), np.float32)
        prob[rng.integers(len(pantheon))] = 1.0  # the target is a Pantheon Move: rows that start at 0
        policies.append((legal, prob))
    outcomes = rng.integers(0, v2.outcome_size, size=n)
    draw = v2.outcome_classes.index("draw")
    values = np.where(outcomes < draw, 1.0, np.where(outcomes == draw, 0.0, -1.0)).astype(np.float32)
    return inputs, policies, values, outcomes


def test_overfit_on_pantheon_positions_from_an_operated_network(manifest, v2, tmp_path):
    """Spec P6's overfit check on Encoding v2: an operated network learns a small set of Pantheon positions."""
    from sevenwd.train import save_checkpoint
    parent = trained_like(manifest, width=128, blocks=2)
    child = operate(parent, manifest, v2)
    init = tmp_path / "pantheon-start.pt"
    save_checkpoint(init, child, torch.optim.AdamW(child.parameters()), 0, v2)
    shard = write_shard(tmp_path / "shard", v2.version, *pantheon_positions(manifest, v2, 64, seed=3))
    config = TrainConfig(steps=400, batch_size=64, device="cpu", weight_decay=0.0)
    result = train(v2, [shard], config, init=init)
    first, last = result.history[0], result.history[-1]
    assert first["total"] > 1.0
    assert last["total"] < 0.05, last
    assert last["policy"] < 0.05 and last["value"] < 0.01 and last["outcome"] < 0.05


def test_a_v1_shard_is_refused_by_a_v2_training(v2, make_shard):
    shard = make_shard(n=8)
    with pytest.raises(ValueError, match="Encoding version 1"):
        train(v2, [shard], TrainConfig(steps=1, width=8, blocks=1, device="cpu"))

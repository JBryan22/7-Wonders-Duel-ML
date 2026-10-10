import numpy as np
import onnxruntime as ort
import pytest
import torch

from sevenwd.export import INPUT_NAME, OUTPUT_NAMES, export_onnx, read_metadata
from sevenwd.network import Network, NetworkConfig
from sevenwd.train import TrainConfig, main, train


def test_overfit_drives_loss_near_zero(manifest, make_shard):
    """The network can learn at all: a small fixed set of positions is memorised (spec P6 validation)."""
    shard = make_shard(n=64, seed=3)
    config = TrainConfig(steps=400, batch_size=64, width=128, blocks=2, device="cpu", weight_decay=0.0)
    result = train(manifest, [shard], config)
    first, last = result.history[0], result.history[-1]
    assert first["total"] > 1.0
    assert last["total"] < 0.05, last
    assert last["policy"] < 0.05 and last["value"] < 0.01 and last["outcome"] < 0.05


def test_exported_onnx_carries_the_encoding_version_and_matches_torch(manifest, tmp_path):
    torch.manual_seed(0)
    net = Network(NetworkConfig.from_manifest(manifest, width=64, blocks=2)).eval()
    path = export_onnx(net, manifest, tmp_path / "net.onnx")

    meta = read_metadata(path)
    assert meta["format"] == "7wd-network"
    assert meta["encoding_version"] == str(manifest.version)
    assert int(meta["input_size"]) == manifest.input_size
    assert int(meta["policy_size"]) == manifest.policy_size
    assert meta["outcome_classes"].split(",") == list(manifest.outcome_classes)

    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    assert session.get_modelmeta().custom_metadata_map["encoding_version"] == str(manifest.version)
    assert [i.name for i in session.get_inputs()] == [INPUT_NAME]
    assert [o.name for o in session.get_outputs()] == list(OUTPUT_NAMES)
    for batch in (1, 7):
        x = np.random.default_rng(batch).random((batch, manifest.input_size), dtype=np.float32)
        policy, value, outcome = session.run(None, {INPUT_NAME: x})
        assert policy.shape == (batch, manifest.policy_size)
        assert value.shape == (batch,)
        assert outcome.shape == (batch, manifest.outcome_size)
        with torch.no_grad():
            expected = net(torch.from_numpy(x))
        for got, want in zip((policy, value, outcome), expected):
            assert np.allclose(got, want.numpy(), atol=1e-4)


def test_cli_exports_a_random_network_then_trains_and_resumes(make_shard, tmp_path):
    ckpt, gen0, gen1 = tmp_path / "latest.pt", tmp_path / "gen-0.onnx", tmp_path / "gen-1.onnx"
    small = ["--width", "32", "--blocks", "1", "--device", "cpu", "--log-every", "0"]
    assert main(["--steps", "0", "--out", str(gen0), "--checkpoint", str(ckpt), *small]) == 0
    assert read_metadata(gen0)["encoding_version"] == "1"
    assert torch.load(ckpt, weights_only=True)["step"] == 0

    make_shard("shards/gen-0", n=16)
    args = ["--data", str(tmp_path / "shards"), "--steps", "3", "--batch-size", "8", "--checkpoint", str(ckpt),
            "--out", str(gen1), *small]
    assert main(args) == 0
    assert main(args) == 0
    saved = torch.load(ckpt, weights_only=True)
    assert saved["step"] == 6 and saved["network"]["width"] == 32
    assert read_metadata(gen1)["width"] == "32"


def test_cli_needs_data_to_train(tmp_path):
    with pytest.raises(SystemExit):
        main(["--steps", "5", "--out", str(tmp_path / "x.onnx")])

import json

import numpy as np
import onnxruntime as ort

from sevenwd.export import INPUT_NAME, read_metadata
from sevenwd.manifest import default_manifest_path, load_manifest
from sevenwd.test_models import DEFAULT_DIR, WRONG_VERSION, fixed_inputs, write_test_models, write_v2_test_models


def test_test_models_are_what_the_csharp_tests_expect(manifest, tmp_path):
    write_test_models(tmp_path, manifest)
    assert read_metadata(tmp_path / "tiny.onnx")["encoding_version"] == str(manifest.version)
    assert read_metadata(tmp_path / "zero.onnx")["encoding_version"] == str(manifest.version)
    assert read_metadata(tmp_path / "wrong-version.onnx")["encoding_version"] == str(WRONG_VERSION)

    x = fixed_inputs(manifest).numpy()
    zero = ort.InferenceSession(str(tmp_path / "zero.onnx"), providers=["CPUExecutionProvider"])
    policy, value, outcome = zero.run(None, {INPUT_NAME: x})
    assert not policy.any() and not value.any() and not outcome.any()  # uniform policy, value 0

    expected = json.loads((tmp_path / "tiny.expected.json").read_text(encoding="utf-8"))
    tiny = ort.InferenceSession(str(tmp_path / "tiny.onnx"), providers=["CPUExecutionProvider"])
    policy, value, outcome = tiny.run(None, {INPUT_NAME: x})
    assert np.allclose(policy, expected["policy"], atol=1e-5)
    assert np.allclose(value, expected["value"], atol=1e-5)
    assert np.allclose(outcome, expected["outcome"], atol=1e-5)


def test_committed_test_models_match_their_expected_outputs(manifest):
    """The fixtures in engine/tests/Agents.Tests/Models are current with the committed manifest."""
    assert read_metadata(DEFAULT_DIR / "tiny.onnx")["encoding_version"] == str(manifest.version)
    assert int(read_metadata(DEFAULT_DIR / "tiny.onnx")["input_size"]) == manifest.input_size
    expected = json.loads((DEFAULT_DIR / "tiny.expected.json").read_text(encoding="utf-8"))
    tiny = ort.InferenceSession(str(DEFAULT_DIR / "tiny.onnx"), providers=["CPUExecutionProvider"])
    policy, value, _ = tiny.run(None, {INPUT_NAME: fixed_inputs(manifest).numpy()})
    assert np.allclose(policy, expected["policy"], atol=1e-5)
    assert np.allclose(value, expected["value"], atol=1e-5)


def test_the_v2_test_model_is_a_uniform_encoding_v2_network(tmp_path):
    v2 = load_manifest(default_manifest_path(2))
    write_v2_test_models(tmp_path, v2)
    for path in (tmp_path / "zero-v2.onnx", DEFAULT_DIR / "zero-v2.onnx"):
        metadata = read_metadata(path)
        assert metadata["encoding_version"] == "2"
        assert (int(metadata["input_size"]), int(metadata["policy_size"])) == (v2.input_size, v2.policy_size)
        session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        policy, value, outcome = session.run(None, {INPUT_NAME: fixed_inputs(v2).numpy()})
        assert policy.shape == (2, v2.policy_size)
        assert not policy.any() and not value.any() and not outcome.any()


def test_the_surgery_test_models_agree_on_base_shaped_inputs(manifest):
    """surgery-parent*.onnx (v1) and surgery-child*.onnx (its operated v2 copy) answer alike when the Pantheon inputs
    are 0; C#'s SurgeryTests check the same on real base-game positions."""
    v2 = load_manifest(default_manifest_path(2))
    x = fixed_inputs(manifest).numpy()
    wide = np.concatenate([x, np.zeros((len(x), v2.input_size - manifest.input_size), np.float32)], axis=1)
    for suffix in ("", "-no-derived"):
        parent = ort.InferenceSession(str(DEFAULT_DIR / f"surgery-parent{suffix}.onnx"), providers=["CPUExecutionProvider"])
        child = ort.InferenceSession(str(DEFAULT_DIR / f"surgery-child{suffix}.onnx"), providers=["CPUExecutionProvider"])
        assert read_metadata(DEFAULT_DIR / f"surgery-child{suffix}.onnx")["encoding_version"] == "2"
        (pp, pv, po), (cp, cv, co) = parent.run(None, {INPUT_NAME: x}), child.run(None, {INPUT_NAME: wide})
        assert np.abs(pp).max() > 0.1  # a real network, not a zero one
        np.testing.assert_allclose(cp[:, :manifest.policy_size], pp, rtol=1e-4, atol=1e-5)
        np.testing.assert_allclose(cv, pv, rtol=1e-4, atol=1e-5)
        np.testing.assert_allclose(co, po, rtol=1e-4, atol=1e-5)

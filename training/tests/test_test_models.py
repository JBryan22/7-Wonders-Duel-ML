import json

import numpy as np
import onnxruntime as ort

from sevenwd.export import INPUT_NAME, read_metadata
from sevenwd.test_models import DEFAULT_DIR, WRONG_VERSION, fixed_inputs, write_test_models


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

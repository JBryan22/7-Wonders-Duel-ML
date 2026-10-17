import copy
import json

import pytest

from sevenwd.manifest import ManifestError, default_manifest_path, load_manifest, parse_manifest


@pytest.fixture
def raw():
    return json.loads(default_manifest_path().read_text(encoding="utf-8"))


def test_committed_manifest_sizes(manifest):
    assert manifest.version == 1
    assert manifest.input_size == 2417
    assert manifest.policy_size == 1509
    assert manifest.outcome_size == 7
    assert manifest.policy_names[0].startswith("build ")


@pytest.mark.parametrize("vector", ["input", "policy"])
def test_blocks_tile_the_vectors(manifest, vector):
    blocks = getattr(manifest, f"{vector}_blocks")
    size = getattr(manifest, f"{vector}_size")
    covered = [i for b in blocks for i in range(b.offset, b.offset + b.size)]
    assert covered == list(range(size))


def test_rejects_a_gap(raw):
    bad = copy.deepcopy(raw)
    bad["policy"]["blocks"][1]["offset"] += 1
    with pytest.raises(ManifestError, match="tile"):
        parse_manifest(bad)


def test_rejects_a_size_mismatch(raw):
    bad = copy.deepcopy(raw)
    bad["input"]["size"] += 1
    with pytest.raises(ManifestError):
        parse_manifest(bad)


def test_rejects_wrong_outcome_size(raw):
    bad = copy.deepcopy(raw)
    bad["heads"]["outcome"]["size"] = 9
    with pytest.raises(ManifestError, match="outcome"):
        parse_manifest(bad)


def test_rejects_other_formats(raw):
    with pytest.raises(ManifestError, match="format"):
        parse_manifest({**raw, "format": "something-else"})


def test_v2_manifest_is_v1_followed_by_the_pantheon_blocks(manifest):
    """Encoding v2 (spec-pantheon P-A7): every v1 index keeps its name and block; Pantheon blocks are appended."""
    v2 = load_manifest(default_manifest_path(2))
    assert (v2.version, v2.input_size, v2.policy_size) == (2, 3206, 3420)
    assert v2.outcome_classes == manifest.outcome_classes
    assert v2.input_names[: manifest.input_size] == manifest.input_names
    assert v2.policy_names[: manifest.policy_size] == manifest.policy_names
    assert v2.input_blocks[: len(manifest.input_blocks)] == manifest.input_blocks
    assert v2.policy_blocks[: len(manifest.policy_blocks)] == manifest.policy_blocks
    assert {b.name for b in v2.input_blocks[len(manifest.input_blocks):]} >= {"slot-tokens", "divinities", "pantheon-spaces"}

import json

import numpy as np
import pytest

from conftest import synthetic_positions
from sevenwd.shards import ShardDataset, ShardError, find_shards, write_shard


def dataset(manifest, dirs):
    return ShardDataset(dirs, manifest.version, manifest.input_size, manifest.policy_size, manifest.outcome_size)


def test_dense_batches_match_what_was_written(manifest, tmp_path):
    a = synthetic_positions(manifest, 10, seed=1)
    b = synthetic_positions(manifest, 7, seed=2)
    data = dataset(manifest, [write_shard(tmp_path / "a", 1, *a), write_shard(tmp_path / "b", 1, *b)])
    assert len(data) == 17
    picks = np.array([16, 0, 3, 10, 3])
    batch = data.batch(picks)
    for row, pos in enumerate(picks):
        (inputs, policies, values, outcomes), i = (a, pos) if pos < 10 else (b, pos - 10)
        assert np.array_equal(batch.input[row], inputs[i].astype(np.float32))
        legal, prob = policies[i]
        assert set(np.nonzero(batch.legal[row])[0].tolist()) == set(legal.tolist())
        assert np.allclose(batch.policy[row, legal], prob)
        assert batch.policy[row].sum() == pytest.approx(1.0)
        assert batch.value[row] == values[i] and batch.outcome[row] == outcomes[i]


def test_find_shards_searches_folders(make_shard, tmp_path):
    s1 = make_shard("gen-1/part-0")
    s2 = make_shard("gen-2/part-0")
    assert find_shards([tmp_path]) == sorted([s1, s2])
    assert find_shards([s1]) == [s1]


def test_refuses_another_encoding_version(manifest, make_shard):
    shard = make_shard()
    header = json.loads((shard / "shard.json").read_text())
    (shard / "shard.json").write_text(json.dumps({**header, "encoding_version": 2}))
    with pytest.raises(ShardError, match="Encoding version"):
        dataset(manifest, [shard])


def test_refuses_wrong_input_width(manifest, make_shard):
    shard = make_shard()
    np.save(shard / "input.npy", np.zeros((32, 10), np.float16))
    with pytest.raises(ShardError, match="input shape"):
        dataset(manifest, [shard])

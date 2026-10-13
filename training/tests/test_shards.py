import json

import numpy as np
import pytest

from conftest import synthetic_positions
from sevenwd.shards import ShardDataset, ShardError, compact_shard, find_shards, write_shard


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


def test_compacted_shards_give_the_same_batches(manifest, tmp_path):
    a = synthetic_positions(manifest, 50, seed=3)
    b = synthetic_positions(manifest, 20, seed=4)
    dense = dataset(manifest, [write_shard(tmp_path / "d1", 1, *a), write_shard(tmp_path / "d2", 1, *b)])
    s1, s2 = write_shard(tmp_path / "s1", 1, *a), write_shard(tmp_path / "s2", 1, *b)
    assert compact_shard(s1, chunk=7) and compact_shard(s2)  # chunks that don't divide the rows
    assert not (s1 / "input.npy").exists() and (s1 / "input_offset.npy").exists()
    assert not compact_shard(s1)  # already compacted: nothing to do
    sparse = dataset(manifest, [s1, s2])
    assert all(s.sparse for s in sparse.shards) and len(sparse) == len(dense) == 70
    picks = np.random.default_rng(0).integers(0, 70, size=200)
    want, got = dense.batch(picks), sparse.batch(picks)
    for name in ("input", "policy", "legal", "value", "outcome"):
        assert np.array_equal(getattr(want, name), getattr(got, name)), name
    assert (sparse.batch(np.arange(70)).input != 0).sum() == (dense.batch(np.arange(70)).input != 0).sum() > 0


def test_compaction_finishes_after_a_crash_and_refuses_bad_indices(manifest, tmp_path):
    a = synthetic_positions(manifest, 12, seed=5)
    shard = write_shard(tmp_path / "s", 1, *a)
    reference = dataset(manifest, [write_shard(tmp_path / "d", 1, *a)]).batch(np.arange(12)).input
    compact_shard(shard)
    # A crash after the sparse files were written but before input.npy was deleted: the dense file is read; a rerun
    # just deletes it.
    np.save(shard / "input.npy", np.asarray(a[0], np.float16))
    assert not dataset(manifest, [shard]).shards[0].sparse
    assert compact_shard(shard) and not (shard / "input.npy").exists()
    assert np.array_equal(dataset(manifest, [shard]).batch(np.arange(12)).input, reference)
    index = np.load(shard / "input_index.npy")
    index[0] = manifest.input_size
    np.save(shard / "input_index.npy", index)
    with pytest.raises(ShardError, match="input index out of range"):
        dataset(manifest, [shard])

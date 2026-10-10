import numpy as np
import pytest

from sevenwd.manifest import load_manifest
from sevenwd.shards import write_shard


@pytest.fixture(scope="session")
def manifest():
    return load_manifest()


def synthetic_positions(manifest, n, seed=0, legal_moves=(3, 12)):
    """n random positions with one-hot policy targets over a random legal set, and consistent value/outcome."""
    rng = np.random.default_rng(seed)
    inputs = (rng.random((n, manifest.input_size)) < 0.1).astype(np.float16)
    policies = []
    for _ in range(n):
        legal = rng.choice(manifest.policy_size, size=rng.integers(*legal_moves), replace=False)
        prob = np.zeros(len(legal), np.float32)
        prob[rng.integers(len(legal))] = 1.0
        policies.append((legal, prob))
    outcomes = rng.integers(0, manifest.outcome_size, size=n)
    draw = manifest.outcome_classes.index("draw")
    values = np.where(outcomes < draw, 1.0, np.where(outcomes == draw, 0.0, -1.0)).astype(np.float32)
    return inputs, policies, values, outcomes


@pytest.fixture
def make_shard(manifest, tmp_path):
    def make(name="shard", n=32, seed=0, **kw):
        return write_shard(tmp_path / name, manifest.version, *synthetic_positions(manifest, n, seed, **kw))
    return make


@pytest.fixture(scope="session", autouse=True)
def quiet_controls(tmp_path_factory):
    """No desktop notifications from tests, and the current-Run pointer kept out of the real runs/ folder (session
    scope, so it also covers module fixtures; child processes inherit it)."""
    with pytest.MonkeyPatch.context() as mp:
        mp.setenv("SEVENWD_NOTIFY", "0")
        mp.setenv("SEVENWD_CURRENT_RUN", str(tmp_path_factory.mktemp("current") / "current.txt"))
        yield

"""The shards C# Self-Play writes (ticket 22) load in Python and agree with the Game Records next to them.

Runs `7wd selfplay` through `dotnet run`, so it needs the .NET SDK (skipped without it; CI has it).
"""
import json
import shutil
import subprocess
from pathlib import Path

import numpy as np
import pytest

from sevenwd.shards import ShardDataset, find_shards

REPO = Path(__file__).resolve().parents[2]
TINY = REPO / "engine" / "tests" / "Agents.Tests" / "Models" / "tiny.onnx"


@pytest.fixture(scope="module")
def selfplay_dir(tmp_path_factory):
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        pytest.skip("needs the .NET SDK to run 7wd selfplay")
    out = tmp_path_factory.mktemp("selfplay") / "gen-0"
    subprocess.run(
        [dotnet, "run", "--project", str(REPO / "engine" / "src" / "Cli"), "-c", "Release", "--",
         "selfplay", f"{TINY},sims=8,threads=1", "--games", "3", "--promo-rate", "0.5", "--parallel", "3",
         "--out", str(out)],
        check=True, cwd=REPO, timeout=900, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )
    return out


def test_selfplay_shards_load_in_python(manifest, selfplay_dir):
    shards = find_shards([selfplay_dir.parent])
    assert shards == [selfplay_dir / "shard"]
    data = ShardDataset(shards, manifest.version, manifest.input_size, manifest.policy_size, manifest.outcome_size)

    lines = (selfplay_dir / "games.jsonl").read_text(encoding="utf-8").splitlines()
    header = json.loads(lines[0])
    assert header["format"] == "7wd-selfplay-1" and header["encodingVersion"] == manifest.version
    records = sorted((json.loads(line) for line in lines[1:]), key=lambda r: r["game"])
    assert len(records) == 3
    searched = [d for r in records for d in r["searched"]]
    assert len(data) == len(searched) > 0

    batch = data.batch(np.arange(len(data)))
    assert np.isfinite(batch.input).all()
    assert (batch.input.sum(axis=1) > 0).all()
    # Every row's legal Moves carry exactly the record's target (float32, legal-Move order kept by the CSR).
    shard = data.shards[0]
    for i, d in enumerate(searched):
        lo, hi = shard.offset[i], shard.offset[i + 1]
        assert np.array_equal(shard.prob[lo:hi], np.asarray(d["target"], np.float32))
        assert batch.legal[i].sum() == len(d["target"])
    assert np.allclose(batch.policy.sum(axis=1), 1, atol=5e-3)
    assert (batch.policy[~batch.legal] == 0).all()

    # Value and outcome class agree with each other and with each game's result, from the decider's view.
    draw = manifest.outcome_classes.index("draw")
    expected_value = np.where(batch.outcome < draw, 1.0, np.where(batch.outcome == draw, 0.0, -1.0))
    assert np.array_equal(batch.value, expected_value)
    row = 0
    for r in records:
        for d in r["searched"]:
            mover = r["log"].split("\n")[2 + d["entry"]].split(" ")[0]
            me = 0 if mover == "P1" else 1
            want = 0.0 if r["winner"] < 0 else (1.0 if r["winner"] == me else -1.0)
            assert batch.value[row] == want
            row += 1

"""The training loop (ticket 23): Run directories with a frozen config, the window and shard cleanup, and the smoke
test (spec A11): 2 Generations in the tiny config, Self-Play -> training -> ONNX export -> Matches, through the real
C# `7wd` (skipped without the .NET SDK; CI has it)."""
import csv
import json
import shutil
from pathlib import Path

import pytest

from sevenwd.config import ConfigError, RunConfig, load_config, parse_config
from sevenwd.export import read_metadata
from sevenwd.loop import (COLUMNS, Run, RunError, clean_shards, create_run, main, open_run, read_best, read_rows,
                          window_for)
from sevenwd.shards import write_shard

from conftest import synthetic_positions

CONFIGS = Path(__file__).resolve().parents[1] / "configs"
TINY = CONFIGS / "tiny.yaml"


def test_default_config_lists_the_built_in_defaults():
    assert load_config(CONFIGS / "default.yaml") == RunConfig()
    tiny = load_config(TINY)
    assert tiny.selfplay.games == 10 and tiny.selfplay.simulations == 8 and tiny.generations == 2


def test_config_refuses_unknown_and_bad_settings():
    with pytest.raises(ConfigError, match="unknown setting"):
        parse_config({"selfplay": {"gmaes": 10}})
    with pytest.raises(ConfigError, match="unknown setting"):
        parse_config({"windows": 3})
    with pytest.raises(ConfigError, match="gumbel or classic"):
        parse_config({"selfplay": {"mode": "greedy"}})
    assert parse_config({"training": {"learning_rate": 1}}).training.learning_rate == 1.0


def test_a_run_records_its_setup(manifest, tmp_path):
    run = create_run(tmp_path / "run", TINY, manifest)
    record = json.loads((run.root / "run.json").read_text(encoding="utf-8"))
    assert record["encoding_version"] == manifest.version
    assert record["git"]["commit"] and record["machine"]["cores"]
    assert (run.root / "config.yaml").read_bytes() == TINY.read_bytes()
    assert json.loads((run.root / "manifest.json").read_text(encoding="utf-8"))["version"] == manifest.version
    assert read_rows(run) == []
    assert open_run(run.root, manifest=manifest).config == load_config(TINY)
    assert open_run(run.root, TINY, manifest).config.selfplay.games == 10


def test_an_edited_config_refuses_to_start(manifest, tmp_path):
    run = create_run(tmp_path / "run", TINY, manifest)
    frozen = run.root / "config.yaml"
    original = frozen.read_bytes()
    frozen.write_bytes(original.replace(b"games: 10", b"games: 12"))
    with pytest.raises(RunError, match="edited"):
        open_run(run.root, manifest=manifest)
    assert main([str(run.root)]) == 1  # refused before the C# side is needed

    frozen.write_bytes(original)  # put back as it was: the Run starts again
    open_run(run.root, manifest=manifest)

    other = tmp_path / "other.yaml"
    other.write_bytes(original.replace(b"seed: 0", b"seed: 1"))
    with pytest.raises(RunError, match="differs"):
        open_run(run.root, other, manifest)
    assert main([str(run.root), "--config", str(other)]) == 1


def test_a_run_needs_a_config_and_a_new_directory(manifest, tmp_path):
    with pytest.raises(RunError, match="not a Run"):
        open_run(tmp_path / "missing", manifest=manifest)
    (tmp_path / "busy").mkdir()
    (tmp_path / "busy" / "x.txt").write_text("x")
    with pytest.raises(RunError, match="isn't empty"):
        create_run(tmp_path / "busy", TINY, manifest)
    wrong = tmp_path / "v2.yaml"
    wrong.write_text(TINY.read_text(encoding="utf-8").replace("encoding_version: 1", "encoding_version: 2"),
                     encoding="utf-8")
    with pytest.raises(RunError, match="Encoding"):
        create_run(tmp_path / "v2", wrong, manifest)


def test_window_takes_the_newest_generations_and_old_shards_are_deleted(manifest, tmp_path):
    run = create_run(tmp_path / "run", TINY, manifest)  # window_positions 1000
    sizes = {1: 400, 2: 500, 3: 300, 4: 450}
    for g, n in sizes.items():
        write_shard(run.shard_dir(g), manifest.version, *synthetic_positions(manifest, n, seed=g))
        (run.selfplay_dir(g) / "games.jsonl").write_text("records stay\n", encoding="utf-8")

    assert window_for(run, 1) == [(1, 400)]
    assert window_for(run, 2) == [(2, 500), (1, 400)]
    assert window_for(run, 3) == [(3, 300), (2, 500), (1, 400)]  # 800 < 1000: one more
    assert window_for(run, 4) == [(4, 450), (3, 300), (2, 500)]  # 1250 >= 1000 after Generation 2

    assert clean_shards(run, 2) == [1]
    assert not run.shard_dir(1).exists() and run.shard_dir(2).exists()
    assert (run.selfplay_dir(1) / "games.jsonl").exists()
    assert clean_shards(run, 2) == []


@pytest.fixture(scope="module")
def tiny_run(tmp_path_factory):
    if shutil.which("dotnet") is None:
        pytest.skip("needs the .NET SDK to run 7wd selfplay and match")
    root = tmp_path_factory.mktemp("loop") / "tiny"
    assert main([str(root), "--config", str(TINY), "--generations", "2"]) == 0
    return root


def test_smoke_two_tiny_generations(manifest, tiny_run):
    run = Run(tiny_run, load_config(TINY), manifest)
    rows = read_rows(run)
    assert [r["generation"] for r in rows] == ["0", "1", "2"]
    with open(run.csv_path, newline="", encoding="utf-8") as f:
        assert next(csv.reader(f)) == COLUMNS

    # Every Generation's network carries the Run's Encoding version (C# checked it again when loading it).
    for g in range(3):
        assert read_metadata(run.net(g))["encoding_version"] == str(manifest.version)
        assert read_metadata(run.net(g))["width"] == "64"

    for g in (1, 2):
        lines = (run.selfplay_dir(g) / "games.jsonl").read_text(encoding="utf-8").splitlines()
        header = json.loads(lines[0])
        assert header["encodingVersion"] == manifest.version and header["games"] == 10
        assert header["model"] == f"net/gen-{g - 1:04d}.onnx"
        assert len(lines) == 11
        shard = json.loads((run.shard_dir(g) / "shard.json").read_text(encoding="utf-8"))
        assert shard["encoding_version"] == manifest.version
        assert int(rows[g]["selfplay_positions"]) == shard["positions"] > 0
        plan = json.loads(run.train_plan(g).read_text(encoding="utf-8"))
        assert plan["steps"] >= 4 and int(rows[g]["train_steps"]) == plan["steps"]
        assert float(rows[g]["loss_total"]) > 0 and float(rows[g]["policy_entropy"]) > 0
        shares = sum(float(rows[g][f"victory_{v}"]) for v in ("military", "science", "civilian", "draw"))
        assert shares == pytest.approx(1)
    assert int(rows[2]["total_steps"]) == int(rows[1]["train_steps"]) + int(rows[2]["train_steps"])
    assert rows[2]["window_generations"] == "2"  # ~700 positions each, window 1000

    # Matches: Generation 0 against Random (Elo chain start), each Generation against the previous one, the
    # anchor every 2 Generations in the tiny config, and against the best network when that is older.
    def summary(name):
        return json.loads((run.root / "matches" / name / "summary.json").read_text(encoding="utf-8"))

    assert summary("gen-0000-vs-random")["complete"]
    for g in (1, 2):
        m = summary(f"gen-{g:04d}-vs-gen-{g - 1:04d}")
        assert m["complete"] and m["games"] == 10
        assert float(rows[g]["prev_score"]) == pytest.approx(m["winRate"])
        assert float(rows[g]["elo"]) == pytest.approx(float(rows[g - 1]["elo"]) + m["eloDiff"], abs=1e-3)
    assert summary("gen-0002-vs-anchor")["b"] == "mcts:sims=50"
    assert rows[2]["anchor_score"] != "" and rows[1]["anchor_score"] == ""

    best = read_best(run)
    assert best == int(rows[2]["best_generation"])
    # Generation 2 plays the best network separately only when that isn't Generation 1.
    assert (run.root / "matches" / "gen-0002-vs-best-gen-0000").exists() == (rows[1]["best_generation"] == "0")
    assert list((run.root / "tb").glob("events.out.tfevents.*"))


def test_smoke_run_continues_without_redoing_work(manifest, tiny_run):
    before = (tiny_run / "generations.csv").read_text(encoding="utf-8")
    assert main([str(tiny_run), "--generations", "2"]) == 0
    assert (tiny_run / "generations.csv").read_text(encoding="utf-8") == before
    assert main([str(tiny_run), "--config", str(TINY), "--generations", "2"]) == 0

    edited = tiny_run.parent / "edited"
    shutil.copytree(tiny_run, edited)
    config = edited / "config.yaml"
    config.write_text(config.read_text(encoding="utf-8").replace("simulations: 8", "simulations: 16", 1),
                      encoding="utf-8")
    assert main([str(edited), "--generations", "3"]) == 1
    assert len(read_rows(Run(edited, load_config(TINY), manifest))) == 3

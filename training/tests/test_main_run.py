"""Ticket 28's main-Run plumbing: the stronger reference opponent (Matches, status, the plateau test on whichever
fixed opponent has headroom), the warm start from a pilot (init_from: checkpoint, window, lineage), the queue with
skipped pilots, and the main config."""
import csv
import json
import shutil
from pathlib import Path

import numpy as np
import onnx
import pytest
import yaml
from onnx import numpy_helper

from sevenwd import control, stopping
from sevenwd.config import ConfigError, load_config, parse_config
from sevenwd.loop import COLUMNS, append_row, create_run, main, open_run, read_rows
from sevenwd.runstate import RunError, StatusWriter, status_line
from sevenwd.train import load_checkpoint

CONFIGS = Path(__file__).resolve().parents[1] / "configs"
TINY = CONFIGS / "tiny.yaml"


def tiny_with(tmp_path, name="config.yaml", **changes) -> Path:
    """tiny.yaml with some settings changed ("matches.reference": ... for a section's key)."""
    raw = yaml.safe_load(TINY.read_text(encoding="utf-8"))
    for key, value in changes.items():
        section, _, field = key.rpartition("__")
        (raw.setdefault(section, {}) if section else raw)[field] = value
    path = tmp_path / name
    path.write_text(yaml.safe_dump(raw), encoding="utf-8")
    return path


# --- the plateau test uses the fixed opponent with headroom ---------------------------------------------------------

def row(g, anchor=None, ref=None, games=400):
    r = {"generation": str(g)}
    for prefix, score in (("anchor", anchor), ("ref", ref)):
        if score is not None:
            half = 0.98 / games ** 0.5 * (score * (1 - score)) ** 0.5 + 0.005
            r.update({f"{prefix}_score": str(score), f"{prefix}_low": str(score - half),
                      f"{prefix}_high": str(min(1.0, score + half))})
    return r


def rows(anchor: dict, ref: dict, last: int):
    return [row(g, anchor.get(g), ref.get(g)) for g in range(last + 1)]


def test_the_plateau_test_follows_the_reference_once_it_has_more_headroom():
    config = parse_config({"matches": {"reference": "mcts:sims=20000"}})
    # The Anchor is saturated (flat near 97%) while the reference score keeps rising: no plateau.
    anchor = {g: 0.97 for g in range(0, 26, 5)}
    rising = {0: 0.30, 5: 0.36, 10: 0.42, 15: 0.48, 20: 0.55, 25: 0.62}
    assert stopping.headroom_series(config, rows(anchor, rising, 25)).name == "reference"
    assert stopping.check(config, rows(anchor, rising, 25), 10) is None
    # The same Anchor numbers alone would have stopped it.
    assert stopping.check(parse_config({}), rows(anchor, {}, 25), 10).kind == "plateau"
    # A flat reference is a plateau, and the message says which opponent.
    flat = {g: 0.40 for g in rising}
    reason = stopping.check(config, rows(anchor, flat, 25), 10)
    assert reason.kind == "plateau" and "reference Elo" in reason.message
    # Before the reference is measured, the Anchor is used.
    assert stopping.headroom_series(config, rows({0: 0.5}, {}, 3)).name == "Anchor"


def test_reference_settings():
    c = parse_config({"matches": {"reference": "mcts:sims=20000", "reference_pairs": 100}})
    assert (c.matches.reference, c.matches.reference_pairs, c.matches.reference_elo) == ("mcts:sims=20000", 100, None)
    with pytest.raises(ConfigError, match="reference_pairs"):
        parse_config({"matches": {"reference_pairs": 0}})
    with pytest.raises(ConfigError, match="init_from"):
        parse_config({"init": "x.pt", "init_from": "runs/x"})


def test_status_shows_the_reference(tmp_path):
    StatusWriter(tmp_path).update(state="running", elo=1800.2, anchor="88% at Generation 5", anchor_elo=1858.0,
                                  reference="41% at Generation 5", reference_elo=1910.4, best=5)
    line = status_line(tmp_path, "main run")
    assert "Anchor 88% at Generation 5 = Anchor Elo 1858; reference 41% at Generation 5 = Elo 1910; best Generation 5" in line


def test_an_old_runs_csv_keeps_its_columns(manifest, tmp_path):
    run = create_run(tmp_path / "old", TINY, manifest)
    old = [c for c in COLUMNS if not c.startswith("ref_")]
    with open(run.csv_path, "w", newline="", encoding="utf-8") as f:
        csv.writer(f).writerow(old)
    append_row(run, {"generation": 0, "elo": 160.0, "ref_score": 0.5})
    with open(run.csv_path, newline="", encoding="utf-8") as f:
        lines = list(csv.reader(f))
    assert lines[0] == old and len(lines[1]) == len(old)
    assert read_rows(run)[0]["elo"] == "160"


# --- the warm start ------------------------------------------------------------------------------------------------

def test_init_from_refuses_what_it_cannot_continue(manifest, tmp_path):
    with pytest.raises(RunError, match="is not a Run"):
        create_run(tmp_path / "child", tiny_with(tmp_path, init_from=str(tmp_path / "nothing")), manifest)
    parent = create_run(tmp_path / "parent", TINY, manifest)
    with pytest.raises(RunError, match="no training checkpoint"):
        create_run(tmp_path / "child", tiny_with(tmp_path, init_from=str(parent.root)), manifest)
    assert not (tmp_path / "child").exists()


def test_a_warm_started_run_continues_its_parent(manifest, tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("needs the .NET SDK to run 7wd selfplay and match")
    parent = tmp_path / "parent"
    assert main([str(parent), "--config", str(TINY), "--generations", "2"]) == 0
    parent_step = load_checkpoint(parent / "net" / "latest.pt", manifest)["step"]
    parent_shards = sorted(p.parent.name for p in (parent / "selfplay").glob("gen-*/shard"))

    # A window big enough for the child's Generation 1 plus both of the parent's shards.
    config = tiny_with(tmp_path, "child.yaml", init_from=str(parent), training__window_positions=100_000,
                       matches__generation0_opponent="mcts:sims=50", matches__anchor_elo=1000.0,
                       matches__reference="mcts:sims=100", matches__reference_elo=1100.0,
                       matches__reference_pairs=3, matches__anchor_every=1)
    child = tmp_path / "child"
    assert main([str(child), "--config", str(config), "--generations", "1"]) == 0

    record = json.loads((child / "run.json").read_text(encoding="utf-8"))["init_from"]
    assert record["run"] == str(parent) and record["checkpoint_step"] == parent_step
    assert record["network_generation"] == 2 and record["finished_generations"] == 3
    assert record["best"]["generation"] in (0, 1, 2) and len(record["checkpoint_sha256"]) == 64

    # The child's first network is the parent's: the same weights as the parent's newest exported Generation (the
    # one its checkpoint holds), tensor for tensor.
    parent_net, child_net = (onnx.load(str(p)) for p in (parent / "net" / "gen-0002.onnx", child / "net" / "gen-0000.onnx"))
    parent_weights = {t.name: numpy_helper.to_array(t) for t in parent_net.graph.initializer}
    child_weights = {t.name: numpy_helper.to_array(t) for t in child_net.graph.initializer}
    assert parent_weights.keys() == child_weights.keys() and len(parent_weights) > 4
    for name, w in parent_weights.items():
        assert np.array_equal(w, child_weights[name]), name
    # ... and it isn't trivially so: the parent's Generation 1 differs.
    first = {t.name: numpy_helper.to_array(t) for t in onnx.load(str(parent / "net" / "gen-0001.onnx")).graph.initializer}
    assert any(not np.array_equal(w, first[name]) for name, w in parent_weights.items())

    rows = read_rows(open_run(child))
    # Generation 0 is measured against the Anchor (the shared Elo scale) and the reference.
    assert rows[0]["anchor_score"] != "" and rows[0]["ref_score"] != "" and rows[0]["elo"] != ""
    assert rows[1]["ref_score"] != "" and rows[1]["ref_elo"] != ""
    assert json.loads((child / "matches" / "gen-0000-vs-reference" / "summary.json").read_text())["b"] == "mcts:sims=100"

    plan = json.loads((child / "net" / "gen-0001.train.json").read_text(encoding="utf-8"))
    assert plan["start_step"] == parent_step  # training goes on from the parent's step count (and optimiser)
    assert plan["window"] == [1] and plan["inherited"] == {"run": str(parent), "window": [2, 1]}
    assert int(rows[1]["window_generations"]) == 3
    # The parent's shards are only read.
    assert sorted(p.parent.name for p in (parent / "selfplay").glob("gen-*/shard")) == parent_shards


# --- the queue and the main config ---------------------------------------------------------------------------------

def test_skipped_pilots_are_shown_and_start_goes_on_to_the_main_run(tmp_path, monkeypatch, manifest):
    monkeypatch.setenv("SEVENWD_QUEUE", str(tmp_path / "queue.yaml"))
    monkeypatch.setenv("SEVENWD_CURRENT_RUN", str(tmp_path / "current.txt"))
    (tmp_path / "queue.yaml").write_text(
        "runs:\n"
        f"  - run: {(tmp_path / 'one').as_posix()}\n    config: {TINY.as_posix()}\n"
        f"  - run: {(tmp_path / 'three').as_posix()}\n    config: {TINY.as_posix()}\n    skip: owner's call\n"
        f"  - name: main run\n    run: {(tmp_path / 'main').as_posix()}\n    config: {TINY.as_posix()}\n",
        encoding="utf-8")
    one = create_run(tmp_path / "one", TINY, manifest).root
    (one / "status.json").write_text(json.dumps({"state": "stopped", "stop_kind": "cap", "reason": "cap"}),
                                     encoding="utf-8")
    control.set_current(one)
    lines = control.status_lines()
    assert lines[1] == "  three: skipped (owner's call)" and lines[2] == "  main run: queued, not started yet (tiny.yaml)"
    assert lines[-1] == "(Start training moves on to main run)"
    assert control.pick_from_queue(lambda s: None) == ((tmp_path / "main").resolve(), TINY)


def test_the_main_config_continues_pilot_1_with_the_stronger_reference():
    main_config = load_config(CONFIGS / "main.yaml")
    desktop = load_config(CONFIGS / "desktop.yaml")
    assert main_config.selfplay == desktop.selfplay and main_config.network == desktop.network
    assert main_config.training == desktop.training and main_config.dropped_inputs == ()
    assert main_config.matches.mode == "classic" and main_config.matches.anchor == "mcts"
    assert main_config.init_from == "C:/7wd-runs/pilot-1-small" and main_config.init is None
    assert main_config.matches.reference and main_config.matches.generation0_opponent == "mcts"
    assert (main_config.stop.cap_hours, main_config.stop.plateau_generations) == (150, 20)

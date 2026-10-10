"""Starting, pausing and stopping training (ticket 24, spec A7 "Control" and "Budget and stopping"): the stopping
rule, pausing inside training, the Run's status line, `7wd-train start | pause | status` and the self-stops through
the loop. The pause/resume test runs the real loop in the background through the C# `7wd` (skipped without .NET)."""
import csv
import json
import shutil
import sys
import time
from pathlib import Path

import pytest

from sevenwd import control, loop as loop_module, stopping
from sevenwd.config import parse_config
from sevenwd.loop import Loop, LoopStopped, create_run, gen_name, main, read_rows
from sevenwd.runstate import (LineCounter, RunLock, StatusWriter, is_running, read_overrides, read_status,
                              request_pause, status_line)
from sevenwd.shards import write_shard
from sevenwd.train import TrainConfig, load_checkpoint, train

from conftest import synthetic_positions

CONFIGS = Path(__file__).resolve().parents[1] / "configs"
TINY = CONFIGS / "tiny.yaml"


def config_file(tmp_path, extra: str, name="config.yaml") -> Path:
    path = tmp_path / name
    path.write_text(TINY.read_text(encoding="utf-8") + "\n" + extra, encoding="utf-8")
    return path


# --- the stopping rule -----------------------------------------------------------------------------------------------

def anchor_row(g, score, games=400, **extra):
    half = 1.96 * (score * (1 - score) / games) ** 0.5
    return {"generation": str(g), "anchor_score": str(score), "anchor_low": str(score - half),
            "anchor_high": str(score + half), **extra}


def rows_with_anchor(scores: dict[int, float], last: int) -> list[dict]:
    return [anchor_row(g, scores[g]) if g in scores else {"generation": str(g)} for g in range(last + 1)]


def test_the_cap_stops_at_the_desktop_hour_budget():
    config = parse_config({})
    assert stopping.check(config, [], 299.9) is None
    reason = stopping.check(config, [], 300.0)
    assert reason.kind == "cap" and "300" in reason.message
    assert stopping.check(config, [], 300.0, ignore=["cap"]) is None
    assert stopping.check(parse_config({"stop": {"cap_hours": None}}), [], 1e6) is None


def test_plateau_means_no_rise_beyond_the_error_bar_over_20_generations():
    config = parse_config({})  # 200 pairs, anchor every 5, plateau over 20
    rising = {5: 0.30, 10: 0.38, 15: 0.46, 20: 0.52, 25: 0.58}
    assert stopping.check(config, rows_with_anchor(rising, 25), 10) is None
    flat = {5: 0.40, 10: 0.42, 15: 0.41, 20: 0.43, 25: 0.42}
    reason = stopping.check(config, rows_with_anchor(flat, 25), 10)
    assert reason.kind == "plateau" and "4 Anchor Matches" in reason.message
    assert stopping.check(config, rows_with_anchor(flat, 24), 10) is None  # no measurement 20 Generations back yet
    assert stopping.check(config, rows_with_anchor(flat, 25), 10, at_start=True).kind == "plateau"
    assert stopping.check(config, rows_with_anchor(flat, 25), 10, ignore=["plateau"]) is None
    # A late jump above the error bar is progress.
    assert stopping.check(config, rows_with_anchor({**flat, 25: 0.55}, 25), 10) is None


def test_warm_start_triggers_fire_on_their_own_generation_only():
    config = parse_config({"stop": {"plateau_generations": None}})
    rows = [{"generation": str(g), "selfplay_games": "2000", "victory_science": "0.004"} for g in range(11)]
    reason = stopping.check(config, rows, 10)
    assert reason.kind == "science" and reason.warm_start and "0.4%" in reason.message
    assert stopping.check(config, rows[:10], 10) is None
    assert stopping.check(config, rows + [{"generation": "11"}], 10) is None  # owner chose to go on: no repeat
    assert stopping.check(config, rows, 10, at_start=True) is None
    rows[8]["victory_science"] = "0.05"  # 1.3% over Generations 6-10
    assert stopping.check(config, rows, 10) is None

    flat = rows_with_anchor({0: 0.10, 5: 0.11, 10: 0.10, 15: 0.12, 20: 0.11}, 20)
    reason = stopping.check(config, flat, 10)
    assert reason.kind == "no-progress" and "Generation 20" in reason.message
    assert stopping.check(config, rows_with_anchor({5: 0.10, 10: 0.2, 20: 0.3}, 20), 10) is None


# --- pausing inside training --------------------------------------------------------------------------------------

def test_training_stops_on_tick_and_resumes_from_its_checkpoint(manifest, make_shard, tmp_path):
    shard = make_shard(n=64)
    ckpt = tmp_path / "latest.pt"
    config = TrainConfig(steps=10, batch_size=16, width=16, blocks=1, device="cpu")
    result = train(manifest, [shard], config, checkpoint=ckpt, tick=lambda step: step >= 3)
    assert result.stopped and result.step == 3 and len(result.history) == 3
    assert load_checkpoint(ckpt, manifest)["step"] == 3
    again = train(manifest, [shard], TrainConfig(steps=7, batch_size=16, device="cpu"), checkpoint=ckpt)
    assert not again.stopped and again.step == 10


def test_a_pause_inside_training_keeps_the_steps_done(manifest, tmp_path, monkeypatch):
    run = create_run(tmp_path / "run", TINY, manifest)
    lp = Loop(run, None, say=lambda m: None)
    lp.initial_network()
    write_shard(run.shard_dir(1), manifest.version, *synthetic_positions(manifest, 600, seed=1))
    window = loop_module.window_for(run, 1)

    calls = []
    monkeypatch.setattr(loop_module, "pause_requested", lambda root: calls.append(1) or len(calls) == 2)
    with pytest.raises(LoopStopped, match="pause requested; training stopped at step 2 of 5"):
        lp.train_generation(1, window, 600)  # 600 new positions x 2 / 256 -> 5 steps
    plan = json.loads(run.train_plan(1).read_text(encoding="utf-8"))
    assert plan["partial_steps"] == 2 and not run.net(1).exists()
    assert load_checkpoint(run.checkpoint, manifest)["step"] == 2

    monkeypatch.setattr(loop_module, "pause_requested", lambda root: False)
    row = lp.train_generation(1, window, 600)
    plan = json.loads(run.train_plan(1).read_text(encoding="utf-8"))
    assert run.net(1).exists() and "partial_steps" not in plan
    assert row["train_steps"] == 5 and row["total_steps"] == 5 and row["loss_total"] > 0


# --- status ---------------------------------------------------------------------------------------------------------

def test_one_run_at_a_time_and_the_lock_goes_with_the_process(tmp_path):
    assert not is_running(tmp_path)
    lock = RunLock(tmp_path)
    assert lock.acquire()
    assert is_running(tmp_path)
    assert not RunLock(tmp_path).acquire()
    lock.release()
    assert not is_running(tmp_path)


def test_status_counts_desktop_hours_across_sessions(tmp_path):
    first = StatusWriter(tmp_path)
    first.base_seconds = 3600 * 2
    first.update(state="paused", finished_generations=4, elo=812.4, best=3, cap_hours=300.0,
                 doing="Generation 4: Self-Play", reason="pause requested")
    second = StatusWriter(tmp_path)
    assert 7200 <= second.seconds() < 7300
    assert second.fields["finished_generations"] == 4

    line = status_line(tmp_path, "main")
    assert line.startswith("main: paused (pause requested): during Generation 4: Self-Play")
    assert "4 Generations done" in line and "Elo 812" in line and "best Generation 3" in line
    assert "2.0 of 300 desktop-hours" in line and "\n" not in line

    second.update(state="stopped", reason="plateau: the Anchor Elo rose +3 (error bar 35) over the last 20 "
                                          "Generations", stop_kind="plateau")
    assert status_line(tmp_path, "main").startswith("main: stopped (plateau: the Anchor Elo rose +3")
    second.update(state="running")
    assert status_line(tmp_path, "main").startswith("main: interrupted")  # says running, but no loop holds the lock
    assert status_line(tmp_path / "nothing", "x") == "x: not started yet"


def test_line_counter_reads_only_what_was_added(tmp_path):
    path = tmp_path / "games.jsonl"
    count = LineCounter(path)
    assert count() == 0
    path.write_text("header\n1\n", encoding="utf-8")
    assert count() == 2
    with open(path, "a", encoding="utf-8") as f:
        f.write("2\n3\n")
    assert count() == 4
    path.write_text("header\n", encoding="utf-8")  # rewritten shorter (a resume dropped a torn line)
    assert count() == 1


# --- self-stops through the loop -------------------------------------------------------------------------------------

def fake_cli(tmp_path) -> str:
    path = tmp_path / "not-7wd.txt"
    path.write_text("not a program", encoding="utf-8")
    return str(path)


def test_a_run_stops_itself_on_a_plateau_and_says_why(manifest, tmp_path, monkeypatch):
    config = config_file(tmp_path, "stop:\n  plateau_generations: 1\n")
    root = tmp_path / "run"
    told = []
    monkeypatch.setattr(loop_module, "notify", lambda title, message: told.append((title, message)))
    monkeypatch.setattr(Loop, "generation", lambda self, g: {k: float(v) if k.startswith("anchor") else g
                                                             for k, v in anchor_row(g, 0.5, games=10).items()})
    assert main([str(root), "--config", str(config), "--cli", fake_cli(tmp_path)]) == loop_module.SELF_STOPPED

    status = read_status(root)
    assert status["state"] == "stopped" and status["stop_kind"] == "plateau"
    assert told and "plateau" in told[0][1]
    with open(root / "generations.csv", newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    assert [r["generation"] for r in rows] == ["0", "1"]
    assert rows[0]["stop_reason"] == "" and rows[1]["stop_reason"].startswith("plateau")
    assert float(rows[1]["hours"]) >= 0
    assert status_line(root).startswith("run: stopped (plateau")
    assert not is_running(root)

    # Starting again finds the rule still holding and stops at once, without a second notification.
    assert main([str(root), "--cli", fake_cli(tmp_path)]) == loop_module.SELF_STOPPED
    assert len(told) == 1 and len(read_rows(loop_module.open_run(root))) == 2


def test_the_cap_holds_at_start_unless_ignored_and_a_repeated_failure_is_a_fault(manifest, tmp_path, monkeypatch):
    config = config_file(tmp_path, "stop:\n  cap_hours: 0.5\n")
    root = tmp_path / "run"
    create_run(root, config, manifest)
    (root / "status.json").write_text(json.dumps({"state": "paused", "active_seconds": 3600}), encoding="utf-8")
    assert main([str(root), "--cli", fake_cli(tmp_path)]) == loop_module.SELF_STOPPED
    assert read_status(root)["stop_kind"] == "cap"

    told = []
    monkeypatch.setattr(loop_module, "notify", lambda title, message: told.append(message))
    monkeypatch.setattr(loop_module, "RETRY_DELAY", 0.0)
    # Going on anyway: recorded; then the Generation 0 Match can't run (the "7wd" is a text file), is tried once
    # more, and the Run stops on an unrecoverable fault.
    assert main([str(root), "--cli", fake_cli(tmp_path), "--ignore-stop"]) == 1
    assert read_overrides(root) == ["cap"]
    status = read_status(root)
    assert status["state"] == "stopped" and status["stop_kind"] == "fault" and status["reason"].startswith("fault")
    assert len(told) == 1
    log = (root / "loop.log").read_text(encoding="utf-8")
    assert "trying again" in log and "unrecoverable fault" in log


# --- 7wd-train ---------------------------------------------------------------------------------------------------

def test_controls_need_a_run(tmp_path, capsys):
    assert control.main(["status"]) == 1
    assert "no current Run" in capsys.readouterr().err
    assert control.main(["start", "--run", str(tmp_path / "new")]) == 1
    assert "give --config" in capsys.readouterr().err
    edited = tmp_path / "edited"
    create_run(edited, TINY)
    cfg = edited / "config.yaml"
    cfg.write_text(cfg.read_text(encoding="utf-8").replace("games: 10", "games: 11"), encoding="utf-8")
    assert control.main(["start", "--run", str(edited)]) == 1
    assert "edited" in capsys.readouterr().err
    assert not is_running(edited)


@pytest.mark.skipif(sys.platform != "win32", reason="desktop shortcuts are Windows-only")
def test_shortcuts_are_made(tmp_path):
    assert control.main(["shortcuts", "--dir", str(tmp_path)]) == 0
    assert (tmp_path / "Start training.lnk").is_file() and (tmp_path / "Pause training.lnk").is_file()


def wait_for(condition, seconds, what):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if condition():
            return
        time.sleep(0.2)
    raise AssertionError(f"timed out waiting for {what}")


def records(run_root: Path, g: int) -> int:
    path = run_root / "selfplay" / gen_name(g) / "games.jsonl"
    return max(0, len(path.read_text(encoding="utf-8").splitlines()) - 1) if path.exists() else 0


def test_pause_during_self_play_frees_the_machine_and_start_continues(tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("needs the .NET SDK to run 7wd selfplay and match")
    # Self-Play long enough to pause in the middle: 40 games, 2 at a time.
    config = config_file(tmp_path, "").read_text(encoding="utf-8")
    config = config.replace("games: 10", "games: 40").replace("parallel: 10", "parallel: 2")
    path = tmp_path / "pause.yaml"
    path.write_text(config, encoding="utf-8")
    root = tmp_path / "run"
    lines = []
    assert control.start(root, path, generations=1, say=lines.append) == 0
    assert control.current_run() == root.resolve()
    assert any(l.startswith("started") for l in lines), lines

    wait_for(lambda: records(root, 1) >= 2 or not is_running(root), 300, "Self-Play to get going")
    assert is_running(root), (root / "loop.out").read_text(encoding="utf-8")
    lines.clear()
    started = time.monotonic()
    assert control.pause(None, wait=60, say=lines.append) == 0
    took = time.monotonic() - started
    assert not is_running(root) and took < 30, (took, lines)
    status = read_status(root)
    assert status["state"] == "paused" and "Self-Play" in status["doing"], status
    done = records(root, 1)
    assert 2 <= done < 40
    assert not (root / "selfplay" / gen_name(1) / "shard").exists()
    assert "paused" in lines[-1] and "Self-Play" in lines[-1]
    assert not (root / "pause.request").exists()

    # Pausing again is harmless; start (the current Run) continues where it stopped, keeping the finished games.
    assert control.pause(None, say=lines.append) == 0 and lines[-2] == "not running"
    head = (root / "selfplay" / gen_name(1) / "games.jsonl").read_text(encoding="utf-8").splitlines()[:done + 1]
    assert control.start(None, generations=1, say=lines.append) == 0
    wait_for(lambda: not is_running(root), 300, "the Run to finish")
    status = read_status(root)
    assert status["state"] == "done", (status, (root / "loop.out").read_text(encoding="utf-8"))
    after = (root / "selfplay" / gen_name(1) / "games.jsonl").read_text(encoding="utf-8").splitlines()
    assert after[0] == head[0] and set(head[1:]) <= set(after) and len(after) == 41  # resumed: kept (sorted)
    assert [r["generation"] for r in read_rows(loop_module.open_run(root))] == ["0", "1"]
    assert status_line(root).startswith("run: done (finished Generation 1")
    assert status["active_seconds"] > 0

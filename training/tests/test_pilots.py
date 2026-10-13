"""Ticket 27's pilot plumbing: desktop-hours that don't count sleep, keeping the computer awake while training, the
pilot queue behind the desktop shortcuts, the pilot configs, and networks without the derived features."""
import json
import shutil
import sys
from pathlib import Path

import pytest
import torch

from sevenwd import awake, control, loop as loop_module
from sevenwd.config import ConfigError, load_config, parse_config
from sevenwd.export import export_onnx, read_metadata
from sevenwd.loop import Loop, create_run, main, read_rows
from sevenwd.network import Network, NetworkConfig
from sevenwd.runstate import ActiveClock, RunError, StatusWriter, read_status, status_line
from sevenwd.train import TrainConfig, train

CONFIGS = Path(__file__).resolve().parents[1] / "configs"
TINY = CONFIGS / "tiny.yaml"


class FakeClock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t


def config_file(tmp_path, extra: str, name="config.yaml") -> Path:
    path = tmp_path / name
    path.write_text(TINY.read_text(encoding="utf-8") + "\n" + extra, encoding="utf-8")
    return path


# --- desktop-hours don't count sleep ---------------------------------------------------------------------------------

def test_a_long_gap_between_heartbeats_is_not_counted(tmp_path):
    clock = FakeClock()
    active = ActiveClock(clock)
    clock.t += 10
    assert active.read() == 10
    active.gap = 300  # the heartbeat reads it every 15 s
    clock.t += 15
    assert active.read() == 25
    clock.t += 15 * 3600  # the machine slept and the clock still counted it
    assert active.read() == 25 and active.skipped == 15 * 3600
    clock.t += 15
    assert active.read() == 40

    # Through the status: base hours carried over, the sleep not added.
    status_clock = FakeClock()
    first = StatusWriter(tmp_path, clock=status_clock)
    first.base_seconds = 11143.0
    first.start_heartbeat()
    first.stop_heartbeat()
    status_clock.t += 60
    first.update(state="running")
    status_clock.t += 54154  # 21:04 -> 12:07
    first.update(state="paused")
    assert read_status(tmp_path)["active_seconds"] == 11203.0


def test_the_desktop_clock_runs(tmp_path):
    from sevenwd.runstate import awake_clock
    clock = awake_clock()
    if sys.platform == "win32":
        assert clock.__name__ == "unbiased"  # QueryUnbiasedInterruptTime: stops while Windows sleeps
    a = clock()
    b = clock()
    assert 0 <= b - a < 1


def test_generation_seconds_come_from_the_desktop_clock(manifest, tmp_path, monkeypatch):
    clock = FakeClock()
    root = tmp_path / "run"
    run = create_run(root, config_file(tmp_path, "generations: 0\n"), manifest)
    status = StatusWriter(root, clock=clock)

    def generation(self, g):
        clock.t += 42  # the clock that skips sleep says 42 s, whatever the wall clock did
        return {"generation": g}

    monkeypatch.setattr(Loop, "generation", generation)
    Loop(run, None, say=lambda m: None, status=status).run_until(0)
    assert float(read_rows(run)[0]["seconds"]) == 42


# --- keeping the computer awake --------------------------------------------------------------------------------------

@pytest.mark.skipif(sys.platform != "win32", reason="SetThreadExecutionState is Windows-only")
def test_keep_awake_holds_off_idle_sleep_until_released(monkeypatch):
    monkeypatch.setenv("SEVENWD_KEEP_AWAKE", "1")
    held = awake.ES_CONTINUOUS | awake.ES_SYSTEM_REQUIRED
    with awake.KeepAwake() as k:
        assert k.how and "Windows" in k.how
        assert awake.set_thread_execution_state(held) == held  # (setting it again returns what was there)
    assert awake.set_thread_execution_state(awake.ES_CONTINUOUS) == awake.ES_CONTINUOUS  # released


def test_keep_awake_can_be_switched_off(monkeypatch):
    monkeypatch.setenv("SEVENWD_KEEP_AWAKE", "0")
    k = awake.KeepAwake()
    assert k.acquire() is None
    k.release()


def test_the_loop_keeps_the_computer_awake_only_while_it_runs(manifest, tmp_path, monkeypatch):
    events = []

    class Recorder:
        how = None

        def acquire(self):
            events.append("acquire")
            self.how = "test"
            return self.how

        def release(self):
            events.append("release")

    def generation(self, g):
        events.append(f"generation {g}")
        return {"generation": g}

    monkeypatch.setattr(loop_module, "KeepAwake", Recorder)
    monkeypatch.setattr(Loop, "generation", generation)
    config = config_file(tmp_path, "generations: 1\n")
    cli = tmp_path / "not-7wd.txt"
    cli.write_text("x", encoding="utf-8")
    assert main([str(tmp_path / "run"), "--config", str(config), "--cli", str(cli)]) == 0
    assert events == ["acquire", "generation 0", "generation 1", "release"]
    assert "keeping the computer awake" in (tmp_path / "run" / "loop.log").read_text(encoding="utf-8")


# --- the pilot queue -------------------------------------------------------------------------------------------------

@pytest.fixture
def queue(tmp_path, monkeypatch):
    """Two pilots on the tiny config: the first exists and is paused, the second doesn't exist yet."""
    monkeypatch.setenv("SEVENWD_QUEUE", str(tmp_path / "queue.yaml"))
    monkeypatch.setenv("SEVENWD_CURRENT_RUN", str(tmp_path / "current.txt"))
    second = config_file(tmp_path, "seed: 1\n".replace("seed: 1", ""), "second.yaml")
    (tmp_path / "queue.yaml").write_text(
        f"pilots:\n  - run: one\n    config: {TINY.as_posix()}\n"
        f"  - name: pilot-two\n    run: {(tmp_path / 'two').as_posix()}\n    config: {second.as_posix()}\n",
        encoding="utf-8")
    one = tmp_path / "one"
    create_run(one, TINY)
    (one / "status.json").write_text(json.dumps({"state": "paused", "reason": "pause requested", "cap_hours": 20,
                                                 "finished_generations": 4, "active_seconds": 7200}),
                                     encoding="utf-8")
    control.set_current(one)
    return tmp_path


def test_status_shows_every_pilot_and_marks_the_current_one(queue, capsys):
    assert control.main(["status"]) == 0
    lines = capsys.readouterr().out.splitlines()
    assert lines[0].startswith("> one: paused (pause requested)") and "2.0 of 20 desktop-hours" in lines[0]
    assert "4 Generations done" in lines[0]
    assert lines[1] == "  pilot-two: queued, not started yet (second.yaml)"
    assert len(lines) == 2
    assert control.main(["status", "--run", str(queue / "one")]) == 0
    assert capsys.readouterr().out.startswith("one: paused")


def fake_spawn(root, extra):
    class Done:
        pid = 1

        def poll(self):
            return 0
    return Done()


def test_start_continues_the_current_pilot_then_moves_on_when_it_is_finished(queue, monkeypatch):
    monkeypatch.setattr(control, "spawn_loop", lambda root, extra: (spawned.append(root), fake_spawn(root, extra))[1])
    spawned, said = [], []
    assert control.start(None, say=said.append) == 0
    assert spawned == [(queue / "one").resolve()]  # paused: start continues it

    one = queue / "one"
    (one / "status.json").write_text(json.dumps({"state": "stopped", "stop_kind": "cap", "cap_hours": 20,
                                                 "reason": "cap: 20.0 desktop-hours used (cap 20)",
                                                 "active_seconds": 72000}), encoding="utf-8")
    assert control.queue_after(one).name == "pilot-two"
    assert loop_module.next_pilot_hint(one) == ". Start training moves on to the next pilot, pilot-two"
    lines = control.status_lines()
    assert lines[0].startswith("> one: stopped (cap") and lines[-1] == "(Start training moves on to pilot-two)"

    spawned.clear()
    assert control.start(None, say=said.append) == 0
    two = (queue / "two").resolve()
    assert spawned == [two] and control.current_run() == two
    assert (two / "config.yaml").is_file()  # created from the queue's config
    assert any("moving on to the next pilot in the queue: pilot-two" in s for s in said)

    # Starting again continues pilot two (it isn't finished), never goes back to one.
    spawned.clear()
    assert control.start(None, say=said.append) == 0
    assert spawned == [two]

    (two / "status.json").write_text(json.dumps({"state": "done", "reason": "finished Generation 2"}),
                                     encoding="utf-8")
    with pytest.raises(RunError, match="every pilot in the queue"):
        control.start(None, say=said.append)
    assert control.status_lines()[-1] == "(every pilot in the queue is finished)"


def test_a_faulted_pilot_or_a_run_outside_the_queue_is_not_skipped(queue, monkeypatch):
    one = queue / "one"
    (one / "status.json").write_text(json.dumps({"state": "stopped", "stop_kind": "fault", "reason": "fault: x"}),
                                     encoding="utf-8")
    assert control.pick_from_queue() == (one.resolve(), None)
    other = queue / "other"
    create_run(other, TINY)
    control.set_current(other)
    assert control.pick_from_queue() is None
    assert control.status_lines()[0].startswith("> other: not started yet")


def test_without_a_queue_status_is_one_line(tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("SEVENWD_QUEUE", str(tmp_path / "missing.yaml"))
    monkeypatch.setenv("SEVENWD_CURRENT_RUN", str(tmp_path / "current.txt"))
    create_run(tmp_path / "solo", TINY)
    control.set_current(tmp_path / "solo")
    assert control.main(["status"]) == 0
    assert capsys.readouterr().out == "solo: not started yet\n"


def test_status_shows_the_anchor_elo(tmp_path):
    StatusWriter(tmp_path).update(state="paused", elo=1391.4, anchor="57% at Generation 2", anchor_elo=1562.7)
    assert "Elo 1391; Anchor 57% at Generation 2 = Anchor Elo 1563" in status_line(tmp_path, "p")


# --- the pilot configs -----------------------------------------------------------------------------------------------

def test_the_pilots_differ_from_the_baseline_only_where_they_should():
    small = load_config(CONFIGS / "pilot-small.yaml")
    desktop = load_config(CONFIGS / "desktop.yaml")
    assert small.selfplay == desktop.selfplay and small.network == desktop.network
    assert (small.stop.cap_hours, small.matches.anchor_every, small.stop.plateau_generations) == (20, 2, None)
    medium = load_config(CONFIGS / "pilot-medium.yaml")
    assert (medium.network.width, medium.network.blocks, medium.selfplay.games) == (512, 6, 40_000)
    assert medium.selfplay.device == medium.matches.device == "cuda"
    classic = load_config(CONFIGS / "pilot-classic.yaml")
    assert classic.selfplay.mode == "classic" and "noise=0.25" in classic.selfplay.search
    no_derived = load_config(CONFIGS / "pilot-no-derived.yaml")
    assert no_derived.dropped_inputs == ("derived",)
    for c in (medium, classic, no_derived):
        assert c.stop == small.stop and c.matches.mode == "classic" and c.matches.anchor_every == 2
        assert c.training == small.training and c.seed == small.seed
    assert no_derived.selfplay == small.selfplay and no_derived.network == small.network
    assert classic.network == small.network and small.dropped_inputs == ()


# --- without the derived features ------------------------------------------------------------------------------------

def test_dropped_inputs_config(manifest, tmp_path):
    assert parse_config({"dropped_inputs": ["derived"]}).dropped_inputs == ("derived",)
    assert parse_config({"dropped_inputs": None}).dropped_inputs == ()
    with pytest.raises(ConfigError, match="list of names"):
        parse_config({"dropped_inputs": "derived"})
    with pytest.raises(RunError, match="not an input block"):
        create_run(tmp_path / "bad", config_file(tmp_path, "dropped_inputs: [derivd]\n"), manifest)
    run = create_run(tmp_path / "ok", config_file(tmp_path, "dropped_inputs: [derived]\n", "ok.yaml"), manifest)
    assert json.loads((run.root / "run.json").read_text(encoding="utf-8"))["dropped_inputs"] == ["derived"]
    plain = create_run(tmp_path / "plain", TINY, manifest)
    assert "dropped_inputs" not in json.loads((plain.root / "run.json").read_text(encoding="utf-8"))


def test_a_network_without_the_derived_features_ignores_them(manifest, make_shard, tmp_path):
    derived = next(b for b in manifest.input_blocks if b.name == "derived")
    torch.manual_seed(0)
    x = torch.rand(4, manifest.input_size)
    y = x.clone()
    y[:, derived.offset:derived.offset + derived.size] = torch.rand(4, derived.size)

    plain = Network(NetworkConfig.from_manifest(manifest, 16, 1)).eval()
    assert "input_keep" not in plain.state_dict()  # unchanged for every existing network
    assert not torch.allclose(plain(x)[1], plain(y)[1])

    shard = make_shard(n=64)
    checkpoint = tmp_path / "latest.pt"
    result = train(manifest, [shard], TrainConfig(steps=3, batch_size=16, width=16, blocks=1, device="cpu",
                                                  dropped_inputs=("derived",)), checkpoint=checkpoint)
    model = result.model.eval()
    with torch.no_grad():
        assert torch.equal(model(x)[1], model(y)[1]) and torch.equal(model(x)[0], model(y)[0])
        assert model.stem.weight.grad is None or torch.all(
            model.stem.weight.grad[:, derived.offset:derived.offset + derived.size] == 0)

    # The checkpoint remembers it; asking for something else is refused; the export says so.
    again = train(manifest, [shard], TrainConfig(steps=1, batch_size=16, device="cpu", dropped_inputs=("derived",)),
                  checkpoint=checkpoint)
    assert again.model.config.dropped_inputs == ("derived",)
    with pytest.raises(ValueError, match="drops the inputs"):
        train(manifest, [shard], TrainConfig(steps=1, batch_size=16, device="cpu", dropped_inputs=()),
              checkpoint=checkpoint)
    out = export_onnx(again.model, manifest, tmp_path / "no-derived.onnx")
    assert read_metadata(out)["dropped_inputs"] == "derived"
    assert "dropped_inputs" not in read_metadata(export_onnx(plain, manifest, tmp_path / "plain.onnx"))

    import onnxruntime as ort
    session = ort.InferenceSession(str(out), providers=["CPUExecutionProvider"])
    a, b = (session.run(None, {"input": t.numpy()}) for t in (x, y))
    assert (a[1] == b[1]).all()


def test_a_tiny_run_without_the_derived_features_runs_through_7wd(manifest, tmp_path):
    if shutil.which("dotnet") is None:
        pytest.skip("needs the .NET SDK to run 7wd selfplay and match")
    config = config_file(tmp_path, "dropped_inputs: [derived]\n")
    root = tmp_path / "no-derived"
    assert main([str(root), "--config", str(config), "--generations", "1"]) == 0
    rows = read_rows(loop_module.open_run(root))
    assert [r["generation"] for r in rows] == ["0", "1"]
    for g in (0, 1):
        assert read_metadata(root / "net" / f"gen-{g:04d}.onnx")["dropped_inputs"] == "derived"

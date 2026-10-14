"""loop.py (spec A7): the training loop. Runs Generations, calling the C# `7wd` program for Self-Play and Matches.

    uv run sevenwd-loop ../runs/tiny --config configs/tiny.yaml     # create a Run from a config and start it
    uv run sevenwd-loop ../runs/tiny                                 # continue it where it left off
    uv run sevenwd-loop ../runs/tiny --generations 3                 # ... but stop after Generation 3

This runs the loop in the foreground (Ctrl+C pauses it). The owner's controls are `7wd-train start | pause | status`
(control.py), which run this same loop in the background.

A Run is a directory started from a frozen config (see config.py). Starting it again always continues where it left
off; a Run whose config.yaml was edited, or started with a different --config, refuses to start (a changed setup is a
new Run). Layout:

    config.yaml            the frozen config (copied in at creation, never changed)
    run.json               creation record: config hash, Encoding version and manifest hash, git commit, machine
    manifest.json          the Encoding manifest the Run started with
    sessions.jsonl         one line per start: time, git commit, machine
    generations.csv        one row per finished Generation
    best.json              the "best" pointer: the network the Advisor uses
    loop.log               what the loop did, with times
    status.json            what the loop is doing now, desktop-hours used, why it stopped (runstate.py; also
                           loop.lock, loop.pid, pause.request and overrides.json)
    tb/                    TensorBoard (uv run tensorboard --logdir <run>/tb)
    net/latest.pt          the training checkpoint (network, optimiser, step count)
    net/gen-NNNN.onnx      Generation N's network; net/gen-NNNN.train.json its training plan and losses
    selfplay/gen-NNNN/     Generation N's Self-Play (played by Generation N-1's network): games.jsonl, shard/
                           (compacted: sparse inputs, see shards.py). When the shard leaves the training window
                           it is deleted and games.jsonl is gzipped to games.jsonl.gz (gunzip it before using
                           `7wd selfplay-check` or `7wd shard` on it)
    matches/gen-NNNN-vs-*/ Generation N's Matches (match.jsonl, summary.json)

A Run with `init_from` (a warm start from a parent Run, e.g. the main Run from pilot 1) starts with the parent's
training checkpoint copied into net/latest.pt at creation, so Generation 0 is the parent's newest network and training
carries on with its optimiser and step count; until the Run's own shards fill the window, the parent's newest shards
fill the rest of it (read in place, never deleted by this Run). run.json records the lineage (init_from).

Generation 0 is the starting network (random, or `init`), measured against Random so the Elo chain starts on the
shared scale. Generation N >= 1: Self-Play with Generation N-1's network -> training on the window (the newest
Generations' shards holding ~window_positions positions; shards that leave it are deleted, the Game Records stay)
for reuse x new positions / batch steps -> export net/gen-N.onnx -> a Match against Generation N-1 (and against the
best network when that is older), and against the Anchor (and the reference, a stronger fixed opponent, when the
config has one) every anchor_every Generations. No gating: Self-Play always
uses the newest network; the best pointer moves when a Generation beats the current best under the pass rule.

Every step skips work already done (the C# side resumes Self-Play and Matches by itself), so stopping and starting
again loses only work in progress. A pause request (the file pause.request, or Ctrl+C) stops the loop at the next
step and inside steps: Self-Play and Matches get it as --stop-file and stop within about a second, keeping finished
games and seed pairs; training checks it after every step and saves its checkpoint. After every Generation the
stopping rule (stopping.py) may stop the Run itself: plateau, cap, a warm-start trigger; a Generation that fails twice
is an unrecoverable fault. A self-stop is recorded in status.json, generations.csv (stop_reason) and TensorBoard (text
"stop_reason"), with a desktop notification.

Exit codes: 0 finished (the configured Generations are done), 1 refused or a fault, 2 paused, 3 stopped itself.
"""
from __future__ import annotations

import argparse
import csv
import glob
import gzip
import hashlib
import json
import logging
import math
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import traceback
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable

from . import stopping
from .awake import KeepAwake
from .config import ConfigError, RunConfig, load_config
from .export import export_onnx, read_metadata
from .manifest import REPO_ROOT, Manifest, load_manifest
from .notify import notify
from .runstate import (PAUSE, PID, LineCounter, RunError, RunLock, StatusWriter, add_override, clear_pause,
                       pause_requested, read_overrides)
from .shards import HEADER as SHARD_HEADER
from .shards import compact_shard
from .stopping import StopReason
from .train import TrainConfig, load_checkpoint, train

RUN_FORMAT = "7wd-run-1"
# The ONNX exporter warns about optional packages it doesn't need (torchvision, triton) on every export.
QUIET_LOGGERS = ("torch.onnx._internal.exporter._registration", "torch.utils.flop_counter")
STOPPED = 2        # paused (pause request, Ctrl+C, or a C# step stopped)
SELF_STOPPED = 3   # the stopping rule stopped the Run
RETRIES = 1        # a failing Generation is tried again this many times before it counts as an unrecoverable fault
RETRY_DELAY = 30.0

COLUMNS = [
    "generation", "finished", "selfplay_games", "selfplay_positions", "selfplay_seconds", "selfplay_positions_per_s",
    "victory_military", "victory_science", "victory_civilian", "victory_draw",
    "window_generations", "window_positions", "train_steps", "total_steps", "train_seconds", "train_steps_per_s",
    "loss_total", "loss_policy", "loss_value", "loss_outcome", "policy_entropy",
    "prev_score", "prev_low", "prev_high", "prev_elo_diff",
    "best_opponent", "best_score", "best_low", "best_passes",
    "anchor_score", "anchor_low", "anchor_high", "anchor_elo",
    "ref_score", "ref_low", "ref_high", "ref_elo",
    "elo", "best_generation", "seconds", "hours", "stop_reason",
]


class LoopStopped(Exception):
    """The loop was paused (a pause request, Ctrl+C, or a C# step that stopped); finished work is kept and the next
    start continues."""


class SelfStopped(Exception):
    """The stopping rule stopped the Run (plateau, cap, warm-start trigger)."""

    def __init__(self, reason: StopReason, tell: bool = True):
        super().__init__(reason.message)
        self.reason = reason
        self.tell = tell  # show a notification (not when a start finds the Run already stopped)


def gen_name(g: int) -> str:
    return f"gen-{g:04d}"


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def manifest_hash(path: Path) -> str:
    """Hash of the manifest's content (parsed JSON, so line endings from a git checkout don't matter)."""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    return sha256_bytes(json.dumps(raw, sort_keys=True, separators=(",", ":")).encode())


def write_json(path: Path, data) -> None:
    """Atomically (temp file + rename)."""
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def git_info() -> dict:
    def git(*args):
        try:
            return subprocess.run(["git", *args], cwd=REPO_ROOT, capture_output=True, text=True, timeout=30,
                                  check=True).stdout.strip()
        except (OSError, subprocess.SubprocessError):
            return None
    status = git("status", "--porcelain")
    return {"commit": git("rev-parse", "HEAD"), "dirty": bool(status) if status is not None else None}


def machine_info() -> dict:
    info = {"name": platform.node(), "platform": platform.platform(), "processor": platform.processor(),
            "cores": os.cpu_count(), "python": platform.python_version()}
    try:
        import torch
        info["torch"] = torch.__version__
        info["cuda"] = torch.cuda.get_device_name(0) if torch.cuda.is_available() else None
    except Exception:  # pragma: no cover - informational only
        pass
    return info


def now() -> str:
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")


# --- Run directories -------------------------------------------------------------------------------------------------

@dataclass
class Run:
    root: Path
    config: RunConfig
    manifest: Manifest

    @property
    def config_path(self) -> Path:
        return self.root / "config.yaml"

    @property
    def parent(self) -> Path | None:
        """The Run this one continues from (config init_from), or None."""
        return resolve_parent(self.config.init_from) if self.config.init_from else None

    @property
    def checkpoint(self) -> Path:
        return self.root / "net" / "latest.pt"

    @property
    def csv_path(self) -> Path:
        return self.root / "generations.csv"

    @property
    def best_path(self) -> Path:
        return self.root / "best.json"

    def net(self, g: int) -> Path:
        return self.root / "net" / f"{gen_name(g)}.onnx"

    def train_plan(self, g: int) -> Path:
        return self.root / "net" / f"{gen_name(g)}.train.json"

    def selfplay_dir(self, g: int) -> Path:
        return self.root / "selfplay" / gen_name(g)

    def shard_dir(self, g: int) -> Path:
        return self.selfplay_dir(g) / "shard"

    def match_dir(self, g: int, opponent: str) -> Path:
        # An Agent spec can hold characters a Windows folder name can't (mcts:sims=50): keep [A-Za-z0-9._=,-].
        return self.root / "matches" / f"{gen_name(g)}-vs-{re.sub(r'[^A-Za-z0-9._=,-]', '-', opponent)}"

    @property
    def pause_path(self) -> Path:
        return self.root / PAUSE

    def rel(self, path: Path) -> str:
        """A path inside the Run as the C# side gets it: relative to the Run, forward slashes."""
        return path.relative_to(self.root).as_posix()


def create_run(root: str | Path, config_path: str | Path, manifest: Manifest | None = None) -> Run:
    """Start a new Run in an empty (or missing) directory from a config file, freezing a copy of it."""
    root = Path(root)
    if (root / "config.yaml").exists():
        raise RunError(f"{root} is already a Run")
    if root.exists() and any(root.iterdir()):
        raise RunError(f"{root} exists and isn't empty: a new Run needs a new directory")
    text = Path(config_path).read_bytes()
    config = load_config(config_path)
    manifest = manifest or load_manifest()
    if manifest.version != config.encoding_version:
        raise RunError(f"the config asks for Encoding v{config.encoding_version} but the manifest is v{manifest.version}")
    check_dropped_inputs(config, manifest)
    lineage = check_parent(config, manifest) if config.init_from else None
    root.mkdir(parents=True, exist_ok=True)
    for sub in ("net", "selfplay", "matches"):
        (root / sub).mkdir(exist_ok=True)
    shutil.copyfile(manifest.path, root / "manifest.json")
    if lineage is not None:
        # The warm start: the parent's checkpoint (network, optimiser, step count) becomes this Run's.
        tmp = root / "net" / "latest.pt.tmp"
        shutil.copyfile(Path(lineage["run"]) / "net" / "latest.pt", tmp)
        os.replace(tmp, root / "net" / "latest.pt")
    write_json(root / "run.json", {
        "format": RUN_FORMAT,
        "created": now(),
        "config_sha256": sha256_bytes(text),
        "config_source": str(Path(config_path).resolve()),
        "encoding_version": manifest.version,
        "manifest_sha256": manifest_hash(manifest.path),
        **({"dropped_inputs": list(config.dropped_inputs)} if config.dropped_inputs else {}),
        **({"init_from": lineage} if lineage is not None else {}),
        "git": git_info(),
        "machine": machine_info(),
    })
    with open(root / "generations.csv", "w", newline="", encoding="utf-8") as f:
        csv.writer(f).writerow(COLUMNS)
    # The config is copied last: a directory with config.yaml is a complete Run.
    tmp = root / "config.yaml.tmp"
    tmp.write_bytes(text)
    os.replace(tmp, root / "config.yaml")
    return Run(root, config, manifest)


def open_run(root: str | Path, config_path: str | Path | None = None, manifest: Manifest | None = None) -> Run:
    """Open an existing Run, refusing it if its frozen config was edited, a different config is given, or the
    Encoding changed."""
    root = Path(root)
    frozen = root / "config.yaml"
    if not frozen.is_file():
        raise RunError(f"{root} is not a Run (no config.yaml); give --config to start one")
    record = json.loads((root / "run.json").read_text(encoding="utf-8"))
    if record.get("format") != RUN_FORMAT:
        raise RunError(f"{root / 'run.json'}: unknown format {record.get('format')!r}")
    text = frozen.read_bytes()
    if sha256_bytes(text) != record["config_sha256"]:
        raise RunError(f"{frozen} was edited after the Run started. A Run's config is frozen: put it back as it was, "
                       f"or start a new Run (a sibling directory) with the changed config")
    if config_path is not None and Path(config_path).read_bytes() != text:
        raise RunError(f"{config_path} differs from the config {root} was started with. A changed setup is a new "
                       f"Run: give a new directory (or leave out --config to continue this Run)")
    config = load_config(frozen)
    manifest = manifest or load_manifest()
    if manifest.version != record["encoding_version"] or manifest.version != config.encoding_version:
        raise RunError(f"{root} trains Encoding v{record['encoding_version']} but the manifest is "
                       f"v{manifest.version}: a new Encoding needs a new Run")
    if manifest_hash(manifest.path) != record["manifest_sha256"]:
        raise RunError(f"the Encoding manifest {manifest.path} changed since {root} started (same version "
                       f"{manifest.version}): the Encoding must not change inside a Run")
    check_dropped_inputs(config, manifest)
    return Run(root, config, manifest)


def resolve_parent(given: str) -> Path:
    path = Path(given)
    if not path.is_absolute():
        path = (REPO_ROOT / path) if (REPO_ROOT / path).exists() else path.resolve()
    return path


def parent_shards(parent: Path) -> list[tuple[int, int]]:
    """The parent Run's Generations that still have a shard, newest first, with their positions."""
    found = []
    for d in (parent / "selfplay").glob("gen-*"):
        header = d / "shard" / SHARD_HEADER
        if header.is_file() and d.name[4:].isdigit():
            found.append((int(d.name[4:]), int(json.loads(header.read_text(encoding="utf-8"))["positions"])))
    return sorted(found, reverse=True)


def check_parent(config: RunConfig, manifest: Manifest) -> dict:
    """A warm start's parent Run must exist, not be training, have a checkpoint, and train the same Encoding with the
    same network shape. Returns the lineage recorded in run.json."""
    from .runstate import is_running
    parent = resolve_parent(config.init_from)
    if not (parent / "config.yaml").is_file() or not (parent / "run.json").is_file():
        raise RunError(f"init_from: {parent} is not a Run")
    checkpoint = parent / "net" / "latest.pt"
    if not checkpoint.is_file():
        raise RunError(f"init_from: {parent} has no training checkpoint (net/latest.pt)")
    if is_running(parent):
        raise RunError(f"init_from: {parent} is still training; pause it first (its checkpoint must not change)")
    record = json.loads((parent / "run.json").read_text(encoding="utf-8"))
    if record.get("encoding_version") != manifest.version or record.get("manifest_sha256") != manifest_hash(manifest.path):
        raise RunError(f"init_from: {parent} trained another Encoding (or manifest) than this Run's v{manifest.version}")
    ckpt = load_checkpoint(checkpoint, manifest)
    net = ckpt["network"]
    if (net.get("width"), net.get("blocks")) != (config.network.width, config.network.blocks):
        raise RunError(f"init_from: {parent}'s network is {net.get('width')}x{net.get('blocks')}, the config asks for "
                       f"{config.network.width}x{config.network.blocks}")
    if tuple(net.get("dropped_inputs") or ()) != tuple(config.dropped_inputs):
        raise RunError(f"init_from: {parent}'s network drops the inputs {list(net.get('dropped_inputs') or [])}, the "
                       f"config says {list(config.dropped_inputs)}")
    # The Generation whose training ended at the checkpoint's step: the network Generation 0 will be.
    trained = None
    for plan in sorted((parent / "net").glob("gen-*.train.json")):
        if json.loads(plan.read_text(encoding="utf-8")).get("total_steps") == ckpt["step"]:
            trained = int(plan.name[4:8])
    best = parent / "best.json"
    with open(parent / "generations.csv", newline="", encoding="utf-8") as f:
        finished_rows = sum(1 for _ in csv.DictReader(f))
    return {
        "run": str(parent),
        "checkpoint_sha256": sha256_bytes(checkpoint.read_bytes()),
        "checkpoint_step": ckpt["step"],
        "network_generation": trained,  # the checkpoint holds this Generation's network (None: mid-training)
        "finished_generations": finished_rows,
        "best": json.loads(best.read_text(encoding="utf-8")) if best.is_file() else None,
        "config_sha256": record.get("config_sha256"),
        "git": record.get("git"),
        "window_shards": [g for g, _ in parent_shards(parent)],
    }


def check_dropped_inputs(config: RunConfig, manifest: Manifest) -> None:
    """`dropped_inputs` names input blocks of the Encoding manifest (e.g. derived)."""
    blocks = [b.name for b in manifest.input_blocks]
    unknown = [n for n in config.dropped_inputs if n not in blocks]
    if unknown:
        raise RunError(f"dropped_inputs: {', '.join(unknown)} is not an input block of Encoding v{manifest.version} "
                       f"(blocks: {', '.join(blocks)})")


def start_run(root: str | Path, config_path: str | Path | None = None, manifest: Manifest | None = None) -> Run:
    root = Path(root)
    if (root / "config.yaml").exists():
        run = open_run(root, config_path, manifest)
    elif config_path is None:
        raise RunError(f"{root} is not a Run yet: give --config to start one")
    else:
        run = create_run(root, config_path, manifest)
    with open(run.root / "sessions.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"started": now(), "git": git_info(), "machine": machine_info()}) + "\n")
    return run


# --- generations.csv and the best pointer ----------------------------------------------------------------------------

def read_rows(run: Run) -> list[dict[str, str]]:
    with open(run.csv_path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def append_row(run: Run, row: dict) -> None:
    # By the file's own header: a Run started before a column was added keeps its layout (the new column is left out).
    with open(run.csv_path, newline="", encoding="utf-8") as f:
        header = next(csv.reader(f), None) or COLUMNS
    with open(run.csv_path, "a", newline="", encoding="utf-8") as f:
        csv.writer(f).writerow([fmt(row.get(c)) for c in header])
        f.flush()
        os.fsync(f.fileno())


def fmt(v) -> str:
    if v is None:
        return ""
    if isinstance(v, bool):
        return "1" if v else "0"
    if isinstance(v, float):
        return f"{v:.6g}"
    return str(v)


def num(s: str | None) -> float | None:
    return float(s) if s not in (None, "") else None


def read_best(run: Run) -> int | None:
    if not run.best_path.exists():
        return None
    return json.loads(run.best_path.read_text(encoding="utf-8"))["generation"]


def write_best(run: Run, g: int, reason: str) -> None:
    write_json(run.best_path, {"generation": g, "model": run.rel(run.net(g)), "since": now(), "reason": reason})


# --- the sliding window ------------------------------------------------------------------------------------------------

def shard_positions(run: Run, g: int) -> int | None:
    header = run.shard_dir(g) / SHARD_HEADER
    if not header.is_file():
        return None
    return int(json.loads(header.read_text(encoding="utf-8"))["positions"])


def window_for(run: Run, g: int) -> list[tuple[int, int]]:
    """The newest Generations (newest first, ending at g) whose shards hold ~window_positions positions: a Generation
    is in while the newer ones hold fewer than the window. A Generation whose shard is gone ends the window."""
    window, total = [], 0
    for i in range(g, 0, -1):
        if total >= run.config.training.window_positions:
            break
        n = shard_positions(run, i)
        if n is None:
            break
        window.append((i, n))
        total += n
    return window


def inherited_window(run: Run, window: list[tuple[int, int]]) -> list[tuple[int, int]]:
    """A warm-started Run's window continues into its parent's newest shards while the Run's own Generations (all of
    them, back to Generation 1) hold fewer than window_positions: the parent Generations (newest first) that fill it."""
    parent = run.parent
    if parent is None or not window or min(i for i, _ in window) != 1:
        return []
    total, inherited = sum(n for _, n in window), []
    for i, n in parent_shards(parent):
        if total >= run.config.training.window_positions:
            break
        inherited.append((i, n))
        total += n
    return inherited


def clean_shards(run: Run, oldest_kept: int) -> list[int]:
    """Delete the shards of Generations older than the window (they never re-enter it). Game Records stay, gzipped
    (~4.5x smaller: a desktop-hour of Self-Play writes ~0.5 GB of records)."""
    removed = []
    for i in range(1, oldest_kept):
        shard = run.shard_dir(i)
        if shard.exists():
            shutil.rmtree(shard)
            removed.append(i)
        gzip_records(run.selfplay_dir(i))
    return removed


def gzip_records(directory: Path) -> None:
    plain, packed = directory / "games.jsonl", directory / "games.jsonl.gz"
    if not plain.exists():
        return
    tmp = packed.with_name(packed.name + ".tmp")
    with open(plain, "rb") as src, gzip.open(tmp, "wb", compresslevel=6) as dst:
        shutil.copyfileobj(src, dst, 1 << 20)
    os.replace(tmp, packed)
    plain.unlink()


# --- the C# side -------------------------------------------------------------------------------------------------------

class Cli:
    """Runs the C# `7wd` program. Default: build engine/src/Cli (Release) once, then `dotnet <7wd.dll>`; or a given
    command (--cli or SEVENWD_CLI: a 7wd.dll, which runs through dotnet, or an executable)."""

    def __init__(self, command: list[str]):
        self.command = command

    @classmethod
    def locate(cls, given: str | None = None, say: Callable[[str], None] = print) -> "Cli":
        given = given or os.environ.get("SEVENWD_CLI")
        if given:
            if not Path(given).is_file() and shutil.which(given) is None:
                raise RunError(f"no 7wd program at {given}")
            return cls(cls._command_for(given))
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise RunError("the loop needs the .NET SDK (dotnet) to run 7wd, or --cli / SEVENWD_CLI")
        project = REPO_ROOT / "engine" / "src" / "Cli"
        say(f"building {project} (Release)")
        build = subprocess.run([dotnet, "build", str(project), "-c", "Release", "--nologo", "-v", "q"],
                               cwd=REPO_ROOT, capture_output=True, text=True)
        if build.returncode != 0:
            raise RunError(f"building 7wd failed:\n{build.stdout[-3000:]}{build.stderr[-3000:]}")
        dlls = sorted(glob.glob(str(project / "bin" / "Release" / "*" / "7wd.dll")))
        if not dlls:
            raise RunError(f"no 7wd.dll under {project / 'bin' / 'Release'} after building")
        return cls([dotnet, dlls[-1]])

    @staticmethod
    def _command_for(path: str) -> list[str]:
        if path.endswith(".dll"):
            return [shutil.which("dotnet") or "dotnet", path]
        return [path]

    def run(self, args: list[str], cwd: Path, log: Path) -> None:
        """Run one command with its output in `log` (appended); raise on failure, LoopStopped on exit code 2."""
        log.parent.mkdir(parents=True, exist_ok=True)
        with open(log, "a", encoding="utf-8") as out:
            out.write(f"\n$ 7wd {' '.join(args)}   ({now()})\n")
            out.flush()
            try:
                code = subprocess.run([*self.command, *args], cwd=cwd, stdout=out,
                                      stderr=subprocess.STDOUT).returncode
            except OSError as e:
                raise RuntimeError(f"can't run {' '.join(self.command)}: {e}") from e
        if code == STOPPED:
            raise LoopStopped(f"7wd {args[0]} was stopped")
        if code != 0:
            tail = log.read_text(encoding="utf-8", errors="replace")[-3000:]
            raise RuntimeError(f"7wd {args[0]} failed (exit code {code}); its output ({log}):\n{tail}")


# --- one Generation ----------------------------------------------------------------------------------------------------

class Loop:
    def __init__(self, run: Run, cli: Cli, say: Callable[[str], None] | None = None,
                 status: StatusWriter | None = None, ignore_stop_now: bool = False):
        self.run = run
        self.cli = cli
        self.config = run.config
        self._say = say or print
        self.status = status or StatusWriter(run.root)
        self.ignore_stop_now = ignore_stop_now  # --ignore-stop: record the standing stop rules that hold now as ignored

    def check_pause(self) -> None:
        if pause_requested(self.run.root):
            raise LoopStopped("pause requested")

    def doing(self, what: str, progress: Callable[[], str] | None = None) -> None:
        self.status.doing(what, progress)

    def say(self, message: str) -> None:
        line = f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] {message}"
        self._say(line)
        with open(self.run.root / "loop.log", "a", encoding="utf-8") as f:
            f.write(line + "\n")

    def neural_spec(self, net: Path, section) -> str:
        parts = [self.run.rel(net), f"sims={section.simulations}", f"device={section.device}",
                 f"threads={section.threads}"]
        if section.mode == "classic":
            parts.append("mode=classic")
        if section.search:
            parts.append(section.search)
        return ",".join(parts)

    # Generations

    def run_until(self, last: int | None = None) -> int:
        """Run Generations until Generation `last` (default: the config's `generations`, else forever) is done.
        Returns the number of finished Generations. Raises LoopStopped when paused, SelfStopped when the stopping rule
        stops the Run (also when it already holds at start)."""
        last = last if last is not None else self.config.generations
        rows: list[dict] = read_rows(self.run)
        self.status.update(state="running", reason=None, stop_kind=None, cap_hours=self.config.stop.cap_hours,
                           **progress_fields(rows))
        while True:
            reason = stopping.check(self.config, rows, self.status.hours(), read_overrides(self.run.root),
                                    at_start=True)
            if reason is None:
                break
            if not self.ignore_stop_now:
                raise SelfStopped(reason, tell=False)
            add_override(self.run.root, reason.kind)
            self.say(f"ignoring this stop rule from now on, as asked: {reason.message}")

        from torch.utils.tensorboard import SummaryWriter
        writer = SummaryWriter(log_dir=str(self.run.root / "tb"))
        try:
            g = len(rows)
            while last is None or g <= last:
                self.check_pause()
                started = self.status.seconds()  # desktop time: a sleep in the middle doesn't count
                row = self.generation_with_retries(g)
                row["seconds"] = self.status.seconds() - started
                row["finished"] = now()
                row["hours"] = self.status.hours()
                reason = stopping.check(self.config, rows + [row], row["hours"], read_overrides(self.run.root))
                if reason:
                    row["stop_reason"] = reason.message
                append_row(self.run, row)
                rows.append(row)
                board(writer, row)
                if reason:
                    writer.add_text("stop_reason", reason.message, g)
                writer.flush()
                self.status.update(**progress_fields(rows))
                self.say(summary_line(row))
                if reason:
                    raise SelfStopped(reason)
                g += 1
            return g
        finally:
            writer.close()

    def generation_with_retries(self, g: int) -> dict:
        for attempt in range(RETRIES + 1):
            try:
                return self.generation(g)
            except (LoopStopped, KeyboardInterrupt, RunError, ConfigError):
                raise
            except Exception as e:
                if attempt == RETRIES:
                    raise
                self.say(f"Generation {g} failed ({type(e).__name__}: {first_line(e)}); trying again in "
                         f"{RETRY_DELAY:.0f} s")
                with open(self.run.root / "loop.log", "a", encoding="utf-8") as f:
                    f.write(traceback.format_exc())
                deadline = time.monotonic() + RETRY_DELAY
                while time.monotonic() < deadline:
                    self.check_pause()
                    time.sleep(min(1.0, max(0.0, deadline - time.monotonic())))
        raise AssertionError("unreachable")

    def generation(self, g: int) -> dict:
        row: dict = {"generation": g}
        if g == 0:
            self.check_pause()
            self.doing("Generation 0: the starting network")
            self.initial_network()
            if read_best(self.run) is None:
                write_best(self.run, 0, "the starting network")
            row["best_generation"] = 0
            m_cfg = self.config.matches
            opponent = m_cfg.generation0_opponent
            if opponent:
                elo = m_cfg.anchor_elo if opponent == m_cfg.anchor else None
                m = self.match(0, "anchor" if opponent == m_cfg.anchor else opponent, opponent, opponent_elo=elo)
                row["elo"] = m.get("eloA")
                if opponent == m_cfg.anchor:
                    row.update(anchor_cols(m))
            if m_cfg.reference and (self.config.init or self.config.init_from):
                # A trained starting network: measure it against the reference too (the plateau test's first point).
                row.update(self.reference_match(0))
            return row

        prev = read_rows(self.run)[-1]
        self.check_pause()
        row.update(self.selfplay(g))
        window = window_for(self.run, g)
        inherited = inherited_window(self.run, window)
        row["window_generations"] = len(window) + len(inherited)
        row["window_positions"] = sum(n for _, n in window + inherited)
        self.check_pause()
        row.update(self.train_generation(g, window, row["selfplay_positions"], inherited))

        prev_elo = num(prev.get("elo"))
        m = self.match(g, gen_name(g - 1), "neural:" + self.neural_spec(self.run.net(g - 1), self.config.matches), prev_elo)
        row.update(prev_score=m["winRate"], prev_low=m["winRateLow"], prev_high=m["winRateHigh"],
                   prev_elo_diff=m["eloDiff"])
        row["elo"] = prev_elo + m["eloDiff"] if prev_elo is not None else None

        best = read_best(self.run)
        if best == g - 1:
            versus_best = m
        else:
            versus_best = self.match(g, f"best-{gen_name(best)}",
                                     "neural:" + self.neural_spec(self.run.net(best), self.config.matches), None)
        row.update(best_opponent=best, best_score=versus_best["winRate"], best_low=versus_best["winRateLow"],
                   best_passes=versus_best["aPasses"])
        if versus_best["aPasses"]:
            write_best(self.run, g, f"beat Generation {best}: {versus_best['winRate']:.1%} "
                                    f"(95% CI {versus_best['winRateLow']:.1%} to {versus_best['winRateHigh']:.1%})")
            self.say(f"Generation {g} beats the best (Generation {best}): best pointer moved")
        row["best_generation"] = read_best(self.run)

        every = self.config.matches.anchor_every
        if every and g % every == 0:
            a = self.match(g, "anchor", self.config.matches.anchor, self.config.matches.anchor_elo)
            row.update(anchor_cols(a))
            if self.config.matches.reference:
                row.update(self.reference_match(g))

        removed = clean_shards(self.run, min(i for i, _ in window))
        if removed:
            self.say(f"deleted the shards of Generations {', '.join(map(str, removed))} (out of the window)")
        return row

    def reference_match(self, g: int) -> dict:
        m = self.config.matches
        r = self.match(g, "reference", m.reference, m.reference_elo, pairs=m.reference_pairs)
        return {"ref_score": r["winRate"], "ref_low": r["winRateLow"], "ref_high": r["winRateHigh"],
                "ref_elo": r.get("eloA")}

    def initial_network(self) -> None:
        out = self.run.net(0)
        if out.exists():
            return
        c = self.config
        init = Path(c.init) if c.init else None
        if init is not None and not init.is_absolute():
            init = (REPO_ROOT / init) if (REPO_ROOT / init).exists() else init.resolve()
        if c.init_from:
            if not self.run.checkpoint.exists():
                raise RunError(f"{self.run.checkpoint} is missing: a Run with init_from starts from the copy of its "
                               f"parent's checkpoint made at creation")
            self.say(f"Generation 0: continuing from {self.run.parent}'s network (its training checkpoint, step "
                     f"{load_checkpoint(self.run.checkpoint, self.run.manifest)['step']:,})")
        else:
            self.say("Generation 0: " + (f"starting network from {init}" if init else "random starting network"))
        result = train(self.run.manifest, [], TrainConfig(steps=0, width=c.network.width, blocks=c.network.blocks,
                                                          seed=c.seed, device="cpu",
                                                          dropped_inputs=tuple(c.dropped_inputs)),
                       checkpoint=self.run.checkpoint, init=init)
        self.export(result.model, out)

    def export(self, model, out: Path) -> None:
        for name in QUIET_LOGGERS:
            logging.getLogger(name).setLevel(logging.ERROR)
        tmp = out.with_name(out.name + ".tmp")
        export_onnx(model, self.run.manifest, tmp)
        # The Encoding-version handshake on the Python side; C# checks it again when it loads the model.
        metadata = read_metadata(tmp)
        version = metadata.get("encoding_version")
        if version != str(self.run.manifest.version):
            raise RuntimeError(f"{tmp} carries Encoding version {version}, the Run is v{self.run.manifest.version}")
        dropped = metadata.get("dropped_inputs", "")
        if dropped != ",".join(self.config.dropped_inputs):
            raise RuntimeError(f"{tmp} drops the inputs {dropped!r}, the Run's config says "
                               f"{','.join(self.config.dropped_inputs)!r}")
        os.replace(tmp, out)

    def selfplay(self, g: int) -> dict:
        s = self.config.selfplay
        directory = self.run.selfplay_dir(g)
        if shard_positions(self.run, g) is None:
            self.say(f"Generation {g}: Self-Play, {s.games} games with {gen_name(g - 1)}")
            lines = LineCounter(directory / "games.jsonl")
            self.doing(f"Generation {g}: Self-Play", lambda: f"{max(0, lines() - 1):,}/{s.games:,} games")
            args = ["selfplay", self.neural_spec(self.run.net(g - 1), s), "--games", str(s.games),
                    "--out", self.run.rel(directory), "--seed", str(self.config.seed + g * 10_000_000),
                    "--promo-rate", repr(s.promo_rate), "--stop-file", self.run.rel(self.run.pause_path)]
            if s.parallel:
                args += ["--parallel", str(s.parallel)]
            if s.workers:
                args += ["--workers", str(s.workers)]
            self.cli.run(args, self.run.root, directory / "log.txt")
        if compact_shard(self.run.shard_dir(g)):
            self.say(f"Generation {g}: shard compacted (sparse inputs)")
        summary = json.loads((directory / "summary.json").read_text(encoding="utf-8"))
        games = 0
        mix = {"military": 0, "science": 0, "civilian": 0, "draw": 0}
        for r in read_records(directory, self.run.manifest.version):
            games += 1
            mix["draw" if r["winner"] < 0 else str(r["victory"]).lower()] += 1
        if games != s.games:
            raise RuntimeError(f"{directory}: {games} Game Records, expected {s.games}")
        return {
            "selfplay_games": games,
            "selfplay_positions": shard_positions(self.run, g),
            "selfplay_seconds": summary.get("seconds"),
            "selfplay_positions_per_s": summary.get("positionsPerSecond"),
            **{f"victory_{k}": v / games for k, v in mix.items()},
        }

    def train_generation(self, g: int, window: list[tuple[int, int]], new_positions: int,
                         inherited: list[tuple[int, int]] = ()) -> dict:
        t = self.config.training
        out, plan_path = self.run.net(g), self.run.train_plan(g)
        step_now = (load_checkpoint(self.run.checkpoint, self.run.manifest)["step"]
                    if self.run.checkpoint.exists() else 0)
        if plan_path.exists():
            plan = json.loads(plan_path.read_text(encoding="utf-8"))
        else:
            steps = max(t.min_steps, math.ceil(t.reuse * new_positions / t.batch_size))
            plan = {"start_step": step_now, "steps": steps, "window": [i for i, _ in window],
                    "window_positions": sum(n for _, n in [*window, *inherited]), "new_positions": new_positions}
            if inherited:
                plan["inherited"] = {"run": str(self.run.parent), "window": [i for i, _ in inherited]}
            write_json(plan_path, plan)
        if not out.exists():
            end = plan["start_step"] + plan["steps"]
            remaining = end - step_now
            parent = plan.get("inherited")
            shards = [self.run.shard_dir(i) for i in plan["window"]]
            if parent:
                shards += [Path(parent["run"]) / "selfplay" / gen_name(i) / "shard" for i in parent["window"]]
            self.say(f"Generation {g}: training {remaining} steps on {plan['window_positions']:,} positions "
                     f"(Generations {min(plan['window'])}-{max(plan['window'])}"
                     + (f" + {Path(parent['run']).name} Generations {min(parent['window'])}-{max(parent['window'])}"
                        if parent else "") + ")")
            done = [step_now - plan["start_step"]]
            self.doing(f"Generation {g}: training", lambda: f"{done[0]:,}/{plan['steps']:,} steps")

            def tick(step: int) -> bool:
                done[0] = step - plan["start_step"]
                return pause_requested(self.run.root)

            c = self.config
            started = time.monotonic()
            result = train(self.run.manifest, shards,
                           TrainConfig(steps=remaining, batch_size=t.batch_size, learning_rate=t.learning_rate,
                                       weight_decay=t.weight_decay, width=c.network.width, blocks=c.network.blocks,
                                       seed=c.seed + 7919 * g + step_now, device=t.device,
                                       checkpoint_minutes=t.checkpoint_minutes,
                                       dropped_inputs=tuple(c.dropped_inputs)),
                           checkpoint=self.run.checkpoint, tick=tick)
            # Losses are averaged over all of the Generation's steps, also when a pause split them over sessions.
            sums = plan.pop("partial_sums", {})
            counted = plan.pop("partial_steps", 0)
            for h in result.history:
                for k, v in h.items():
                    sums[k] = sums.get(k, 0.0) + v
            counted += len(result.history)
            plan["seconds"] = plan.get("seconds", 0) + time.monotonic() - started
            if result.stopped and result.step < end:
                plan.update(partial_sums=sums, partial_steps=counted)
                write_json(plan_path, plan)
                raise LoopStopped(f"pause requested; training stopped at step {result.step - plan['start_step']} of "
                                  f"{plan['steps']}, checkpoint saved")
            if counted:
                plan["losses"] = {k: v / counted for k, v in sums.items()}
                plan["steps_per_second"] = counted / plan["seconds"] if plan["seconds"] > 0 else None
            plan["total_steps"] = result.step
            self.doing(f"Generation {g}: exporting the network")
            self.export(result.model, out)
            write_json(plan_path, plan)
        losses = plan.get("losses", {})
        return {
            "train_steps": plan["steps"], "total_steps": plan.get("total_steps"),
            "train_seconds": plan.get("seconds"), "train_steps_per_s": plan.get("steps_per_second"),
            "loss_total": losses.get("total"), "loss_policy": losses.get("policy"),
            "loss_value": losses.get("value"), "loss_outcome": losses.get("outcome"),
            "policy_entropy": losses.get("entropy"),
        }

    def match(self, g: int, name: str, opponent: str, opponent_elo: float | None, pairs: int | None = None) -> dict:
        """Generation g's network (A) against the Agent spec `opponent` (B), saved in matches/gen-g-vs-<name>/ and
        resumed if it was stopped. Returns its summary.json."""
        m = self.config.matches
        pairs = pairs or m.pairs
        directory = self.run.match_dir(g, name)
        summary_path = directory / "summary.json"
        if summary_path.exists():
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
            if summary.get("complete"):
                return summary
        self.check_pause()
        self.say(f"Generation {g}: Match against {name} ({pairs} seed pairs)")
        lines = LineCounter(directory / "match.jsonl")
        self.doing(f"Generation {g}: Match against {name}", lambda: f"{max(0, lines() - 1)}/{pairs} seed pairs")
        args = ["match", "neural:" + self.neural_spec(self.run.net(g), m), opponent,
                "--pairs", str(pairs), "--seed", str(self.config.seed), "--out", self.run.rel(directory),
                "--stop-file", self.run.rel(self.run.pause_path)]
        if m.promo:
            args += ["--promo", m.promo]
        if m.parallel:
            args += ["--threads", str(m.parallel)]
        if opponent_elo is not None:
            args += ["--elo-b", repr(float(opponent_elo))]
        self.cli.run(args, self.run.root, directory / "log.txt")
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
        if not summary.get("complete"):
            raise LoopStopped(f"the Match in {directory} is incomplete")
        return summary


def anchor_cols(m: dict) -> dict:
    return {"anchor_score": m["winRate"], "anchor_low": m["winRateLow"], "anchor_high": m["winRateHigh"],
            "anchor_elo": m.get("eloA")}


def read_records(directory: Path, encoding_version: int):
    """The Game Records of a Self-Play directory (games.jsonl, or games.jsonl.gz once gzipped), one at a time: a
    desktop-hour Generation holds ~0.5 GB of them."""
    path = directory / "games.jsonl"
    with (open(path, encoding="utf-8") if path.exists()
          else gzip.open(directory / "games.jsonl.gz", "rt", encoding="utf-8")) as f:
        header = json.loads(f.readline())
        if header.get("encodingVersion") != encoding_version:
            raise RuntimeError(f"{path}: Self-Play encoded v{header.get('encodingVersion')}, "
                               f"the Run is v{encoding_version}")
        for line in f:
            if line.strip():
                yield json.loads(line)


def first_line(e: BaseException) -> str:
    text = str(e).strip()
    return text.splitlines()[0] if text else type(e).__name__


def progress_fields(rows: list[dict]) -> dict:
    """What status shows about the finished Generations: how many, the latest Elo, the latest Anchor result, the
    best pointer."""
    fields: dict = {"finished_generations": len(rows)}
    for r in reversed(rows):
        if stopping.value(r.get("elo")) is not None:
            fields["elo"] = stopping.value(r.get("elo"))
            break
    for r in reversed(rows):
        score = stopping.value(r.get("anchor_score"))
        if score is not None:
            fields["anchor"] = f"{score:.0%} at Generation {r['generation']}"
            fields["anchor_elo"] = stopping.value(r.get("anchor_elo"))
            break
    for r in reversed(rows):
        score = stopping.value(r.get("ref_score"))
        if score is not None:
            fields["reference"] = f"{score:.0%} at Generation {r['generation']}"
            fields["reference_elo"] = stopping.value(r.get("ref_elo"))
            break
    if rows and stopping.value(rows[-1].get("best_generation")) is not None:
        fields["best"] = int(stopping.value(rows[-1]["best_generation"]))
    return fields


SCALARS = {
    "loss/total": "loss_total", "loss/policy": "loss_policy", "loss/value": "loss_value",
    "loss/outcome": "loss_outcome", "policy/entropy": "policy_entropy",
    "elo/generation": "elo", "elo/anchor": "anchor_elo", "match/vs_previous": "prev_score",
    "match/vs_anchor": "anchor_score", "elo/reference": "ref_elo", "match/vs_reference": "ref_score",
    "best/generation": "best_generation",
    "selfplay/victory_military": "victory_military", "selfplay/victory_science": "victory_science",
    "selfplay/victory_civilian": "victory_civilian", "selfplay/victory_draw": "victory_draw",
    "selfplay/positions_per_second": "selfplay_positions_per_s", "training/steps_per_second": "train_steps_per_s",
    "training/window_positions": "window_positions", "time/generation_seconds": "seconds",
}


def board(writer, row: dict) -> None:
    for tag, column in SCALARS.items():
        value = row.get(column)
        if value is not None and value != "":
            writer.add_scalar(tag, float(value), row["generation"])


def summary_line(row: dict) -> str:
    g = row["generation"]
    parts = [f"Generation {g} done in {row['seconds']:.0f} s"]
    if row.get("selfplay_positions") is not None:
        parts.append(f"{row['selfplay_positions']:,} new positions")
    if row.get("loss_total") is not None:
        parts.append(f"loss {row['loss_total']:.3f}")
    if row.get("prev_score") is not None:
        parts.append(f"vs Generation {g - 1}: {row['prev_score']:.1%}")
    if row.get("anchor_score") is not None:
        parts.append(f"vs the Anchor: {row['anchor_score']:.1%}")
    if row.get("ref_score") is not None:
        parts.append(f"vs the reference: {row['ref_score']:.1%}")
    if row.get("elo") is not None:
        parts.append(f"Elo {row['elo']:.0f}")
    parts.append(f"best: Generation {row.get('best_generation')}")
    return "; ".join(parts)


def next_pilot_hint(root: Path) -> str:
    """For a Run in the pilot queue that stopped itself: which pilot the next `7wd-train start` moves on to."""
    try:
        from .control import queue_after
        following = queue_after(root)
    except Exception:  # the hint is informational only
        return ""
    return f". Start training moves on to the next Run in the queue, {following.name}" if following else ""


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="sevenwd-loop", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("run", type=Path, help="the Run directory (e.g. ../runs/main)")
    p.add_argument("--config", type=Path, help="start a new Run from this config (an existing Run must match it)")
    p.add_argument("--generations", type=int, help="stop after this Generation (default: the config's)")
    p.add_argument("--cli", help="the 7wd program to run (a 7wd.dll or executable; default: build engine/src/Cli)")
    p.add_argument("--ignore-stop", action="store_true",
                   help="go on although the cap or the plateau rule says stop (recorded in overrides.json)")
    args = p.parse_args(argv)
    try:
        run = start_run(args.run, args.config)
    except (RunError, ConfigError) as e:
        print(f"sevenwd-loop: {e}", file=sys.stderr)
        return 1

    lock = RunLock(run.root)
    if not lock.acquire():
        print(f"sevenwd-loop: {run.root} is already running (see 7wd-train status / pause)", file=sys.stderr)
        return 1
    clear_pause(run.root)  # a start means go: an old pause request is spent
    (run.root / PID).write_text(str(os.getpid()), encoding="utf-8")
    status = StatusWriter(run.root)
    status.update(state="running", reason=None, stop_kind=None, doing="starting (building 7wd)")
    status.start_heartbeat()
    loop = Loop(run, None, status=status, ignore_stop_now=args.ignore_stop)  # type: ignore[arg-type]
    state, reason, kind, code = "done", None, None, 0
    awake = KeepAwake()  # no idle sleep while the loop runs; released on pause, stop and exit
    try:
        if awake.acquire():
            loop.say(f"keeping the computer awake while training: {awake.how}")
        loop.cli = Cli.locate(args.cli, say=loop.say)
        done = loop.run_until(args.generations)
        reason = f"finished Generation {done - 1}, as configured"
        loop.say(f"stopped after Generation {done - 1}")
        notify(f"7WD training: {run.root.name} finished", reason)
    except SelfStopped as e:
        state, reason, kind, code = "stopped", e.reason.message, e.reason.kind, SELF_STOPPED
        loop.say(f"the Run stopped itself: {reason}")
        if e.tell:
            notify(f"7WD training: {run.root.name} stopped", reason + next_pilot_hint(run.root))
        print(f"sevenwd-loop: stopped: {reason}", file=sys.stderr)
    except (LoopStopped, KeyboardInterrupt) as e:
        state, reason, code = "paused", str(e) or "Ctrl+C", STOPPED
        if pause_requested(run.root) and not reason.startswith("pause requested"):
            reason = "pause requested"  # a C# step that stopped because of it
        loop.say(f"paused ({reason}); start it again to continue")
        print(f"sevenwd-loop: paused ({reason}); start it again to continue", file=sys.stderr)
    except (RunError, ConfigError) as e:
        state, reason, code = "refused", str(e), 1
        print(f"sevenwd-loop: {e}", file=sys.stderr)
    except Exception as e:
        state, reason, kind, code = "stopped", f"fault: {type(e).__name__}: {first_line(e)}", "fault", 1
        with open(run.root / "loop.log", "a", encoding="utf-8") as f:
            f.write(traceback.format_exc())
        loop.say(f"the Run stopped itself on an unrecoverable fault: {first_line(e)} (details in loop.log)")
        notify(f"7WD training: {run.root.name} stopped (fault)", first_line(e))
        print(f"sevenwd-loop: failed: {e}", file=sys.stderr)
    finally:
        awake.release()
        status.stop_heartbeat()
        if state == "paused":
            clear_pause(run.root)
        status.update(state=state, reason=reason, stop_kind=kind)
        try:
            (run.root / PID).unlink()
        except OSError:
            pass
        lock.release()
    return code


if __name__ == "__main__":
    sys.exit(main())

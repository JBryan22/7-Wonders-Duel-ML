"""loop.py (spec A7): the training loop. Runs Generations, calling the C# `7wd` program for Self-Play and Matches.

    uv run sevenwd-loop ../runs/tiny --config configs/tiny.yaml     # create a Run from a config and start it
    uv run sevenwd-loop ../runs/tiny                                 # continue it where it left off
    uv run sevenwd-loop ../runs/tiny --generations 3                 # ... but stop after Generation 3

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
    tb/                    TensorBoard (uv run tensorboard --logdir <run>/tb)
    net/latest.pt          the training checkpoint (network, optimiser, step count)
    net/gen-NNNN.onnx      Generation N's network; net/gen-NNNN.train.json its training plan and losses
    selfplay/gen-NNNN/     Generation N's Self-Play (played by Generation N-1's network): games.jsonl, shard/
    matches/gen-NNNN-vs-*/ Generation N's Matches (match.jsonl, summary.json)

Generation 0 is the starting network (random, or `init`), measured against Random so the Elo chain starts on the
shared scale. Generation N >= 1: Self-Play with Generation N-1's network -> training on the window (the newest
Generations' shards holding ~window_positions positions; shards that leave it are deleted, the Game Records stay)
for reuse x new positions / batch steps -> export net/gen-N.onnx -> a Match against Generation N-1 (and against the
best network when that is older), and against the Anchor every anchor_every Generations. No gating: Self-Play always
uses the newest network; the best pointer moves when a Generation beats the current best under the pass rule.

Every step skips work already done (the C# side resumes Self-Play and Matches by itself), so stopping and starting
again loses only work in progress. Pausing within a step and self-stopping are ticket 24's job.
"""
from __future__ import annotations

import argparse
import csv
import glob
import hashlib
import json
import logging
import math
import os
import platform
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable

from .config import ConfigError, RunConfig, load_config
from .export import export_onnx, read_metadata
from .manifest import REPO_ROOT, Manifest, load_manifest
from .shards import HEADER as SHARD_HEADER
from .train import TrainConfig, load_checkpoint, train

RUN_FORMAT = "7wd-run-1"
# The ONNX exporter warns about optional packages it doesn't need (torchvision, triton) on every export.
QUIET_LOGGERS = ("torch.onnx._internal.exporter._registration", "torch.utils.flop_counter")
STOPPED = 2

COLUMNS = [
    "generation", "finished", "selfplay_games", "selfplay_positions", "selfplay_seconds", "selfplay_positions_per_s",
    "victory_military", "victory_science", "victory_civilian", "victory_draw",
    "window_generations", "window_positions", "train_steps", "total_steps", "train_seconds", "train_steps_per_s",
    "loss_total", "loss_policy", "loss_value", "loss_outcome", "policy_entropy",
    "prev_score", "prev_low", "prev_high", "prev_elo_diff",
    "best_opponent", "best_score", "best_low", "best_passes",
    "anchor_score", "anchor_low", "anchor_high", "anchor_elo",
    "elo", "best_generation", "seconds",
]


class RunError(Exception):
    """The Run can't start as asked (an edited config, a different config, another Encoding version...)."""


class LoopStopped(Exception):
    """A C# step was stopped (Ctrl+C); finished work is kept and the next start continues."""


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
        return self.root / "matches" / f"{gen_name(g)}-vs-{opponent}"

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
    root.mkdir(parents=True, exist_ok=True)
    for sub in ("net", "selfplay", "matches"):
        (root / sub).mkdir(exist_ok=True)
    shutil.copyfile(manifest.path, root / "manifest.json")
    write_json(root / "run.json", {
        "format": RUN_FORMAT,
        "created": now(),
        "config_sha256": sha256_bytes(text),
        "config_source": str(Path(config_path).resolve()),
        "encoding_version": manifest.version,
        "manifest_sha256": manifest_hash(manifest.path),
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
    return Run(root, config, manifest)


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
    with open(run.csv_path, "a", newline="", encoding="utf-8") as f:
        csv.writer(f).writerow([fmt(row.get(c)) for c in COLUMNS])
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


def clean_shards(run: Run, oldest_kept: int) -> list[int]:
    """Delete the shards of Generations older than the window (they never re-enter it). Game Records stay."""
    removed = []
    for i in range(1, oldest_kept):
        shard = run.shard_dir(i)
        if shard.exists():
            shutil.rmtree(shard)
            removed.append(i)
    return removed


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
    def __init__(self, run: Run, cli: Cli, say: Callable[[str], None] | None = None):
        self.run = run
        self.cli = cli
        self.config = run.config
        self._say = say or print

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
        Returns the number of finished Generations."""
        last = last if last is not None else self.config.generations
        from torch.utils.tensorboard import SummaryWriter
        writer = SummaryWriter(log_dir=str(self.run.root / "tb"))
        try:
            g = len(read_rows(self.run))
            while last is None or g <= last:
                started = time.monotonic()
                row = self.generation(g)
                row["seconds"] = time.monotonic() - started
                row["finished"] = now()
                append_row(self.run, row)
                board(writer, row)
                writer.flush()
                self.say(summary_line(row))
                g += 1
            return g
        finally:
            writer.close()

    def generation(self, g: int) -> dict:
        row: dict = {"generation": g}
        if g == 0:
            self.initial_network()
            if read_best(self.run) is None:
                write_best(self.run, 0, "the starting network")
            row["best_generation"] = 0
            opponent = self.config.matches.generation0_opponent
            if opponent:
                m = self.match(0, opponent, opponent, opponent_elo=None)
                row["elo"] = m.get("eloA")
                if opponent == self.config.matches.anchor:
                    row.update(anchor_cols(m))
            return row

        prev = read_rows(self.run)[-1]
        row.update(self.selfplay(g))
        window = window_for(self.run, g)
        row["window_generations"] = len(window)
        row["window_positions"] = sum(n for _, n in window)
        row.update(self.train_generation(g, window, row["selfplay_positions"]))

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

        removed = clean_shards(self.run, min(i for i, _ in window))
        if removed:
            self.say(f"deleted the shards of Generations {', '.join(map(str, removed))} (out of the window)")
        return row

    def initial_network(self) -> None:
        out = self.run.net(0)
        if out.exists():
            return
        c = self.config
        init = Path(c.init) if c.init else None
        if init is not None and not init.is_absolute():
            init = (REPO_ROOT / init) if (REPO_ROOT / init).exists() else init.resolve()
        self.say("Generation 0: " + (f"starting network from {init}" if init else "random starting network"))
        result = train(self.run.manifest, [], TrainConfig(steps=0, width=c.network.width, blocks=c.network.blocks,
                                                          seed=c.seed, device="cpu"),
                       checkpoint=self.run.checkpoint, init=init)
        self.export(result.model, out)

    def export(self, model, out: Path) -> None:
        for name in QUIET_LOGGERS:
            logging.getLogger(name).setLevel(logging.ERROR)
        tmp = out.with_name(out.name + ".tmp")
        export_onnx(model, self.run.manifest, tmp)
        # The Encoding-version handshake on the Python side; C# checks it again when it loads the model.
        version = read_metadata(tmp).get("encoding_version")
        if version != str(self.run.manifest.version):
            raise RuntimeError(f"{tmp} carries Encoding version {version}, the Run is v{self.run.manifest.version}")
        os.replace(tmp, out)

    def selfplay(self, g: int) -> dict:
        s = self.config.selfplay
        directory = self.run.selfplay_dir(g)
        if shard_positions(self.run, g) is None:
            self.say(f"Generation {g}: Self-Play, {s.games} games with {gen_name(g - 1)}")
            args = ["selfplay", self.neural_spec(self.run.net(g - 1), s), "--games", str(s.games),
                    "--out", self.run.rel(directory), "--seed", str(self.config.seed + g * 10_000_000),
                    "--promo-rate", repr(s.promo_rate)]
            if s.parallel:
                args += ["--parallel", str(s.parallel)]
            if s.workers:
                args += ["--workers", str(s.workers)]
            self.cli.run(args, self.run.root, directory / "log.txt")
        summary = json.loads((directory / "summary.json").read_text(encoding="utf-8"))
        records = read_records(directory / "games.jsonl", self.run.manifest.version)
        if len(records) != s.games:
            raise RuntimeError(f"{directory}: {len(records)} Game Records, expected {s.games}")
        mix = {"military": 0, "science": 0, "civilian": 0, "draw": 0}
        for r in records:
            mix["draw" if r["winner"] < 0 else str(r["victory"]).lower()] += 1
        return {
            "selfplay_games": len(records),
            "selfplay_positions": shard_positions(self.run, g),
            "selfplay_seconds": summary.get("seconds"),
            "selfplay_positions_per_s": summary.get("positionsPerSecond"),
            **{f"victory_{k}": v / len(records) for k, v in mix.items()},
        }

    def train_generation(self, g: int, window: list[tuple[int, int]], new_positions: int) -> dict:
        t = self.config.training
        out, plan_path = self.run.net(g), self.run.train_plan(g)
        step_now = (load_checkpoint(self.run.checkpoint, self.run.manifest)["step"]
                    if self.run.checkpoint.exists() else 0)
        if plan_path.exists():
            plan = json.loads(plan_path.read_text(encoding="utf-8"))
        else:
            steps = max(t.min_steps, math.ceil(t.reuse * new_positions / t.batch_size))
            plan = {"start_step": step_now, "steps": steps, "window": [i for i, _ in window],
                    "window_positions": sum(n for _, n in window), "new_positions": new_positions}
            write_json(plan_path, plan)
        if not out.exists():
            remaining = plan["start_step"] + plan["steps"] - step_now
            self.say(f"Generation {g}: training {remaining} steps on {plan['window_positions']:,} positions "
                     f"(Generations {min(plan['window'])}-{max(plan['window'])})")
            c = self.config
            started = time.monotonic()
            result = train(self.run.manifest, [self.run.shard_dir(i) for i in plan["window"]],
                           TrainConfig(steps=remaining, batch_size=t.batch_size, learning_rate=t.learning_rate,
                                       weight_decay=t.weight_decay, width=c.network.width, blocks=c.network.blocks,
                                       seed=c.seed + 7919 * g + step_now, device=t.device,
                                       checkpoint_minutes=t.checkpoint_minutes),
                           checkpoint=self.run.checkpoint)
            seconds = time.monotonic() - started
            if result.history:
                keys = result.history[0].keys()
                plan["losses"] = {k: sum(h[k] for h in result.history) / len(result.history) for k in keys}
                plan["seconds"] = plan.get("seconds", 0) + seconds
                plan["steps_per_second"] = len(result.history) / seconds if seconds > 0 else None
            plan["total_steps"] = result.step
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

    def match(self, g: int, name: str, opponent: str, opponent_elo: float | None) -> dict:
        """Generation g's network (A) against the Agent spec `opponent` (B), saved in matches/gen-g-vs-<name>/ and
        resumed if it was stopped. Returns its summary.json."""
        m = self.config.matches
        directory = self.run.match_dir(g, name)
        summary_path = directory / "summary.json"
        if summary_path.exists():
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
            if summary.get("complete"):
                return summary
        self.say(f"Generation {g}: Match against {name} ({m.pairs} seed pairs)")
        args = ["match", "neural:" + self.neural_spec(self.run.net(g), m), opponent,
                "--pairs", str(m.pairs), "--seed", str(self.config.seed), "--out", self.run.rel(directory)]
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


def read_records(path: Path, encoding_version: int) -> list[dict]:
    lines = path.read_text(encoding="utf-8").splitlines()
    header = json.loads(lines[0])
    if header.get("encodingVersion") != encoding_version:
        raise RuntimeError(f"{path}: Self-Play encoded v{header.get('encodingVersion')}, the Run is v{encoding_version}")
    return [json.loads(line) for line in lines[1:] if line.strip()]


SCALARS = {
    "loss/total": "loss_total", "loss/policy": "loss_policy", "loss/value": "loss_value",
    "loss/outcome": "loss_outcome", "policy/entropy": "policy_entropy",
    "elo/generation": "elo", "elo/anchor": "anchor_elo", "match/vs_previous": "prev_score",
    "match/vs_anchor": "anchor_score", "best/generation": "best_generation",
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
    if row.get("elo") is not None:
        parts.append(f"Elo {row['elo']:.0f}")
    parts.append(f"best: Generation {row.get('best_generation')}")
    return "; ".join(parts)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="sevenwd-loop", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("run", type=Path, help="the Run directory (e.g. ../runs/main)")
    p.add_argument("--config", type=Path, help="start a new Run from this config (an existing Run must match it)")
    p.add_argument("--generations", type=int, help="stop after this Generation (default: the config's)")
    p.add_argument("--cli", help="the 7wd program to run (a 7wd.dll or executable; default: build engine/src/Cli)")
    args = p.parse_args(argv)
    try:
        run = start_run(args.run, args.config)
        loop = Loop(run, None)  # type: ignore[arg-type]
        loop.cli = Cli.locate(args.cli, say=loop.say)
        done = loop.run_until(args.generations)
        loop.say(f"stopped after Generation {done - 1}")
        return 0
    except (RunError, ConfigError) as e:
        print(f"sevenwd-loop: {e}", file=sys.stderr)
        return 1
    except RuntimeError as e:
        print(f"sevenwd-loop: failed: {e}", file=sys.stderr)
        return 1
    except (LoopStopped, KeyboardInterrupt) as e:
        print(f"sevenwd-loop: stopped ({e or 'Ctrl+C'}); start it again to continue", file=sys.stderr)
        return STOPPED


if __name__ == "__main__":
    sys.exit(main())

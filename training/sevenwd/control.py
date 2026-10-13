"""7wd-train: start, pause and check on training (spec A7, "Control: always manual").

    uv run 7wd-train start --run ../runs/main --config configs/main.yaml   # create a Run and start it
    uv run 7wd-train start                  # continue the current Run where it left off
    uv run 7wd-train pause                  # pause it: frees the machine within about a minute
    uv run 7wd-train status                 # one line per Run: what it is doing, how far, the latest Elo, why it stopped
    uv run 7wd-train shortcuts              # (Windows) put "Start training", "Pause training", "Training status" on
                                            # the desktop

Works locally or over SSH (run from training/). `start` runs the loop (loop.py) in the background, detached from the
terminal, so closing the window or the SSH session doesn't stop it; its output goes to <run>/loop.out. The current
Run is remembered in runs/current.txt (or SEVENWD_CURRENT_RUN); --run picks another and makes it current.

Nothing starts or pauses training on its own; the only exception is the Run stopping itself (stopping.py), which
shows a notification and says why in `status`. After a plateau or cap stop, `start --ignore-stop` goes on anyway.

The pilot queue (ticket 27): runs/queue.yaml next to current.txt (or SEVENWD_QUEUE) lists Runs to train one after
the other, each with the config that creates it:

    pilots:
      - run: C:/7wd-runs/pilot-1-small
        config: configs/pilot-small.yaml     # relative to training/
      - run: C:/7wd-runs/pilot-2-medium
        config: configs/pilot-medium.yaml

When the current Run is in the queue and has finished (it stopped itself on its cap or a plateau, or did its
configured Generations), a plain `start` moves on to the next unfinished pilot: it creates that Run if needed, makes
it current and starts it. The loop never moves on by itself (spec A7: control is always manual; a finished pilot is
also the moment to look at its result): its notification says that Start goes on with the next pilot. `status` shows
one line per pilot, the current one marked with ">".
"""
from __future__ import annotations

import argparse
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

from dataclasses import dataclass

from .config import ConfigError
from .manifest import REPO_ROOT
from .runstate import PID, RunError, clear_pause, is_running, read_status, request_pause, status_line

PROG = "7wd-train"
TRAINING_DIR = Path(__file__).resolve().parents[1]


def current_file() -> Path:
    return Path(os.environ.get("SEVENWD_CURRENT_RUN") or REPO_ROOT / "runs" / "current.txt")


def current_run() -> Path | None:
    f = current_file()
    if not f.is_file():
        return None
    text = f.read_text(encoding="utf-8").strip()
    return Path(text) if text else None


def set_current(root: Path) -> None:
    f = current_file()
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_text(str(Path(root).resolve()) + "\n", encoding="utf-8")


def resolve(run: Path | None) -> Path:
    if run is not None:
        return Path(run).resolve()
    current = current_run()
    if current is None:
        raise RunError("no current Run: give --run <dir> (and --config <file> to create one)")
    return current


# --- the pilot queue ------------------------------------------------------------------------------------------------

FINISHED_KINDS = ("cap", "plateau")  # self-stops that end a pilot (warm-start triggers and faults don't)


@dataclass(frozen=True)
class Pilot:
    name: str
    run: Path
    config: Path


def queue_file() -> Path:
    return Path(os.environ.get("SEVENWD_QUEUE") or current_file().parent / "queue.yaml")


def load_queue(path: Path | None = None) -> list[Pilot]:
    """The pilots in the queue file, in order (empty when there is no queue)."""
    import yaml
    path = Path(path) if path is not None else queue_file()
    if not path.is_file():
        return []
    try:
        raw = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    except yaml.YAMLError as e:
        raise RunError(f"{path}: not valid YAML: {e}") from e
    items = raw.get("pilots") if isinstance(raw, dict) else None
    if not isinstance(items, list):
        raise RunError(f"{path}: expected a list under 'pilots:'")
    pilots = []
    for i, item in enumerate(items, 1):
        if not isinstance(item, dict) or not item.get("run") or not item.get("config"):
            raise RunError(f"{path}: pilot {i} needs 'run' (a directory) and 'config' (a file)")
        run = Path(item["run"])
        run = (run if run.is_absolute() else path.parent / run).resolve()
        config = Path(item["config"])
        config = config if config.is_absolute() else TRAINING_DIR / config
        pilots.append(Pilot(str(item.get("name") or run.name), run, config))
    return pilots


def same(a: Path, b: Path) -> bool:
    return os.path.normcase(str(Path(a).resolve())) == os.path.normcase(str(Path(b).resolve()))


def finished(root: Path) -> str | None:
    """Why a Run is finished (cap, plateau, its configured Generations), or None."""
    if not (Path(root) / "config.yaml").exists() or is_running(root):
        return None
    s = read_status(root)
    if s.get("state") == "done" or s.get("state") == "stopped" and s.get("stop_kind") in FINISHED_KINDS:
        return s.get("reason") or s.get("state")
    return None


def queue_after(root: Path, queue: list[Pilot] | None = None) -> Pilot | None:
    """The first unfinished pilot after `root` in the queue (None if `root` isn't queued or nothing is left)."""
    queue = load_queue() if queue is None else queue
    at = next((i for i, p in enumerate(queue) if same(p.run, root)), None)
    if at is None:
        return None
    return next((p for p in queue[at + 1:] if not finished(p.run)), None)


def pick_from_queue(say=print) -> tuple[Path, Path | None] | None:
    """For a plain `start`: the Run to start and, when it doesn't exist yet, its config. None: no queue applies (start
    the current Run as before)."""
    queue = load_queue()
    if not queue:
        return None
    current = current_run()
    if current is not None and not any(same(p.run, current) for p in queue):
        return None  # a Run outside the queue is current: leave it be
    if current is None:
        pilot = next((p for p in queue if not finished(p.run)), None)
    else:
        why = finished(current)
        if why is None:
            return current, None
        pilot = queue_after(current, queue)
        name = next(p.name for p in queue if same(p.run, current))
        if pilot is not None:
            say(f"{name} is finished ({why}); moving on to the next pilot in the queue: {pilot.name}")
    if pilot is None:
        raise RunError(f"every pilot in the queue ({queue_file()}) is finished; see `{PROG} status`")
    return pilot.run, None if (pilot.run / "config.yaml").exists() else pilot.config


def status_lines(run: Path | None = None) -> list[str]:
    """`7wd-train status`: the given Run's line; or, with a queue, one line per pilot (">" = current) plus the
    current Run if it isn't queued."""
    if run is not None:
        root = Path(run).resolve()
        if not (root / "config.yaml").exists():
            raise RunError(f"{root} is not a Run")
        return [status_line(root)]
    queue = load_queue()
    if not queue:
        root = resolve(None)
        if not (root / "config.yaml").exists():
            raise RunError(f"{root} is not a Run")
        return [status_line(root)]
    current = current_run()
    lines = []
    if current is not None and not any(same(p.run, current) for p in queue):
        lines.append("> " + status_line(current) + "  (current, not in the queue)")
    for p in queue:
        mark = "> " if current is not None and same(p.run, current) else "  "
        if (p.run / "config.yaml").exists():
            lines.append(mark + status_line(p.run, p.name))
        else:
            lines.append(mark + f"{p.name}: queued, not started yet ({p.config.name})")
    if current is not None and finished(current):
        following = queue_after(current, queue)
        lines.append(f"(Start training moves on to {following.name})" if following
                     else "(every pilot in the queue is finished)")
    return lines


# --- start ----------------------------------------------------------------------------------------------------------

def spawn_loop(root: Path, extra: list[str]) -> subprocess.Popen:
    """Run the loop in the background, detached: it outlives this command, its window and an SSH session."""
    command = [sys.executable, "-m", "sevenwd.loop", str(root), *extra]
    out = open(root / "loop.out", "a", encoding="utf-8")
    out.write(f"\n$ {' '.join(command)}\n")
    out.flush()
    kwargs = dict(cwd=TRAINING_DIR, stdin=subprocess.DEVNULL, stdout=out, stderr=subprocess.STDOUT)
    try:
        if sys.platform == "win32":
            # A hidden console of its own (its C# children share it, so no windows pop up), its own process group
            # (Ctrl+C in this window doesn't reach it), and out of an SSH session's job when the job allows it.
            flags = subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP
            try:
                return subprocess.Popen(command, creationflags=flags | subprocess.CREATE_BREAKAWAY_FROM_JOB, **kwargs)
            except OSError:
                return subprocess.Popen(command, creationflags=flags, **kwargs)
        return subprocess.Popen(command, start_new_session=True, **kwargs)
    finally:
        out.close()


def start(run: Path | None, config: Path | None = None, generations: int | None = None,
          ignore_stop: bool = False, wait: float = 60.0, say=print) -> int:
    from .loop import create_run, open_run  # imports PyTorch: only start needs it, so pause and status stay quick
    picked = pick_from_queue(say) if run is None and config is None and not ignore_stop else None
    if picked is not None:
        run, config = picked
    root = resolve(run)
    # Check (or create) the Run here, so a refusal is shown to whoever pressed start, not only written to a log.
    if (root / "config.yaml").exists():
        open_run(root, config)
    elif config is None:
        raise RunError(f"{root} is not a Run yet: give --config to start one")
    else:
        create_run(root, config)
        say(f"created the Run {root} from {config}")
    set_current(root)
    if is_running(root):
        say("already running")
        say(status_line(root))
        return 0
    clear_pause(root)
    extra = []
    if generations is not None:
        extra += ["--generations", str(generations)]
    if ignore_stop:
        extra.append("--ignore-stop")
    before = read_status(root).get("updated")
    process = spawn_loop(root, extra)
    # Wait until the loop has taken over (it rewrites status.json) or exited (refused, already stopped...).
    deadline = time.monotonic() + wait
    while time.monotonic() < deadline:
        code = process.poll()
        if code is not None:
            say(status_line(root))
            if code == 3:
                say("(the stopping rule holds; `7wd-train start --ignore-stop` goes on anyway)")
            elif code not in (0, 2):
                say(f"the loop exited with code {code}; see {root / 'loop.out'} and {root / 'loop.log'}")
            return 0 if code in (0, 3) else code
        s = read_status(root)
        if is_running(root) and s.get("updated") != before and s.get("state") == "running":
            say(f"started (process {process.pid})")
            say(status_line(root))
            return 0
        time.sleep(0.2)
    say(f"started (process {process.pid}), but it hasn't reported yet; check `{PROG} status`")
    return 0


# --- pause ----------------------------------------------------------------------------------------------------------

def kill_loop(root: Path) -> bool:
    """Last resort when a pause isn't honoured: end the loop and everything it started."""
    try:
        pid = int((root / PID).read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return False
    try:
        if sys.platform == "win32":
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, timeout=60)
        elif os.getpgid(pid) == pid:
            os.killpg(pid, signal.SIGKILL)
        else:
            os.kill(pid, signal.SIGKILL)
    except (OSError, subprocess.SubprocessError):
        return False
    return True


def pause(run: Path | None, wait: float = 120.0, say=print) -> int:
    root = resolve(run)
    if not (root / "config.yaml").exists():
        raise RunError(f"{root} is not a Run")
    if not is_running(root):
        say("not running")
        say(status_line(root))
        return 0
    request_pause(root)
    say("pausing...")
    started = time.monotonic()
    while time.monotonic() - started < wait:
        if not is_running(root):
            say(f"paused in {time.monotonic() - started:.0f} s")
            say(status_line(root))
            return 0
        time.sleep(0.5)
    say(f"the loop didn't pause within {wait:.0f} s: ending it (finished work is kept on disk)")
    kill_loop(root)
    for _ in range(60):
        if not is_running(root):
            break
        time.sleep(0.5)
    clear_pause(root)
    say(status_line(root))
    return 0 if not is_running(root) else 1


# --- shortcuts ------------------------------------------------------------------------------------------------------

SHORTCUT = r"""
$shell = New-Object -ComObject WScript.Shell
$dir = $env:SEVENWD_SHORTCUT_DIR
if (-not $dir) { $dir = [Environment]::GetFolderPath('Desktop') }
foreach ($item in @(@('Start training', 'start'), @('Pause training', 'pause'), @('Training status', 'status'))) {
    $link = $shell.CreateShortcut((Join-Path $dir ($item[0] + '.lnk')))
    $link.TargetPath = $env:SEVENWD_SHORTCUT_TARGET
    $link.Arguments = $item[1] + ' --hold 20'
    $link.WorkingDirectory = $env:SEVENWD_SHORTCUT_WORKDIR
    $link.Description = '7 Wonders Duel training: ' + $item[1] + ' the current Run'
    $link.Save()
    Write-Output (Join-Path $dir ($item[0] + '.lnk'))
}
"""


def shortcuts(directory: Path | None = None, say=print) -> int:
    if sys.platform != "win32":
        say("the desktop shortcuts are for Windows; on Linux use `uv run 7wd-train start|pause|status`")
        return 1
    target = Path(sys.executable).parent / f"{PROG}.exe"
    if not target.is_file():
        say(f"{target} is missing: run `uv sync` in {TRAINING_DIR} first")
        return 1
    env = {**os.environ, "SEVENWD_SHORTCUT_TARGET": str(target), "SEVENWD_SHORTCUT_WORKDIR": str(TRAINING_DIR)}
    if directory is not None:
        Path(directory).mkdir(parents=True, exist_ok=True)
        env["SEVENWD_SHORTCUT_DIR"] = str(Path(directory).resolve())
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                             "-Command", SHORTCUT], env=env, capture_output=True, text=True, timeout=60)
    if result.returncode != 0:
        say(f"making the shortcuts failed: {result.stderr.strip()}")
        return 1
    say("made: " + "; ".join(l.strip() for l in result.stdout.splitlines() if l.strip()))
    return 0


# --- the command ----------------------------------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog=PROG, description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    s = sub.add_parser("start", help="start (continue) the current Run in the background")
    s.add_argument("--run", type=Path, help="the Run directory (becomes the current Run)")
    s.add_argument("--config", type=Path, help="create the Run from this config (must match an existing Run's)")
    s.add_argument("--generations", type=int, help="stop after this Generation (default: the config's)")
    s.add_argument("--ignore-stop", action="store_true",
                   help="go on although the cap or the plateau rule says stop (recorded in the Run)")
    pa = sub.add_parser("pause", help="pause the current Run (frees the machine within about a minute)")
    pa.add_argument("--run", type=Path)
    pa.add_argument("--wait", type=float, default=120.0, help="seconds to wait before ending it by force")
    st = sub.add_parser("status", help="one line about the current Run (with a pilot queue: one per pilot)")
    st.add_argument("--run", type=Path)
    sh = sub.add_parser("shortcuts", help="(Windows) put Start training / Pause training shortcuts on the desktop")
    sh.add_argument("--dir", type=Path, help="where to put them (default: the desktop)")
    for q in (s, pa, st):
        q.add_argument("--hold", type=float, default=0, help=argparse.SUPPRESS)  # keep a shortcut's window open
    args = p.parse_args(argv)
    try:
        if args.command == "start":
            code = start(args.run, args.config, args.generations, args.ignore_stop)
        elif args.command == "pause":
            code = pause(args.run, args.wait)
        elif args.command == "status":
            for line in status_lines(args.run):
                print(line)
            code = 0
        else:
            return shortcuts(args.dir)
    except (RunError, ConfigError) as e:
        print(f"{PROG}: {e}", file=sys.stderr)
        code = 1
    if getattr(args, "hold", 0):
        print(f"(this window closes in {args.hold:.0f} s)", flush=True)
        time.sleep(args.hold)
    return code


if __name__ == "__main__":
    sys.exit(main())

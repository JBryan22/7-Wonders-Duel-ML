"""What a Run is doing right now, shared by the loop (which writes it) and `7wd-train` (which reads it and asks for a
pause). All of it lives in files in the Run directory, so it works the same on Windows and Linux, locally or over SSH:

    status.json      the loop's state: running / paused / stopped / done, what it is doing, how far it has got, the
                     desktop-hours used, and why it stopped. Rewritten at every step and every ~15 s while running.
    loop.lock        held (an OS file lock) by the running loop; free when no loop runs, even after a crash
    loop.pid         the running loop's process id (to force a pause that doesn't come)
    pause.request    present = please pause: the loop stops at the next step, and passes it to the C# side as
                     --stop-file so Self-Play and Matches stop within about a second
    overrides.json   stop rules the owner chose to ignore (7wd-train start --ignore-stop)
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

STATUS = "status.json"
LOCK = "loop.lock"
PID = "loop.pid"
PAUSE = "pause.request"
OVERRIDES = "overrides.json"


class RunError(Exception):
    """The Run can't start as asked (an edited config, a different config, another Encoding version...)."""


def now() -> str:
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")


def write_json(path: Path, data) -> None:
    """Atomically (temp file + rename). Retries briefly: on Windows a reader (or OneDrive) can hold the file."""
    tmp = path.with_name(path.name + f".{os.getpid()}.tmp")
    tmp.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    for attempt in range(20):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            if attempt == 19:
                raise
            time.sleep(0.05)


def read_json(path: Path, default=None):
    for attempt in range(5):
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return default
        except (PermissionError, json.JSONDecodeError):
            time.sleep(0.05)
    return default


def read_status(root: Path) -> dict:
    return read_json(Path(root) / STATUS, {}) or {}


def pause_requested(root: Path) -> bool:
    return (Path(root) / PAUSE).exists()


def request_pause(root: Path) -> None:
    (Path(root) / PAUSE).write_text(now() + "\n", encoding="utf-8")


def clear_pause(root: Path) -> None:
    try:
        (Path(root) / PAUSE).unlink()
    except FileNotFoundError:
        pass


def read_overrides(root: Path) -> list[str]:
    return list((read_json(Path(root) / OVERRIDES, {}) or {}).get("ignore", []))


def add_override(root: Path, kind: str) -> None:
    data = read_json(Path(root) / OVERRIDES, {}) or {}
    ignore = data.get("ignore", [])
    if kind not in ignore:
        ignore.append(kind)
    data["ignore"] = ignore
    data.setdefault("history", []).append({"ignore": kind, "since": now()})
    write_json(Path(root) / OVERRIDES, data)


# --- the loop lock -----------------------------------------------------------------------------------------------------

class RunLock:
    """An exclusive OS lock on <run>/loop.lock: one loop per Run, and "is it running?" without trusting a pid (the OS
    drops the lock when the process dies, however it dies)."""

    def __init__(self, root: Path):
        self.path = Path(root) / LOCK
        self._file = None

    def acquire(self) -> bool:
        f = open(self.path, "a+b")
        try:
            if sys.platform == "win32":
                import msvcrt
                f.seek(0)
                msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(f.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            f.close()
            return False
        self._file = f
        return True

    def release(self) -> None:
        if self._file is None:
            return
        try:
            if sys.platform == "win32":
                import msvcrt
                self._file.seek(0)
                msvcrt.locking(self._file.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._file.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass
        self._file.close()
        self._file = None


def is_running(root: Path) -> bool:
    if not (Path(root) / LOCK).exists():
        return False
    probe = RunLock(root)
    if probe.acquire():
        probe.release()
        return False
    return True


# --- the status the loop keeps ----------------------------------------------------------------------------------------

class StatusWriter:
    """Keeps <run>/status.json up to date while the loop runs, including the desktop-hours used (counted only while
    the loop runs, carried over from the previous status at start). A heartbeat thread rewrites it every `every`
    seconds so status shows progress inside long steps (and the hours stay right if the machine dies)."""

    def __init__(self, root: Path, every: float = 15.0):
        self.root = Path(root)
        previous = read_status(self.root)
        self.base_seconds = float(previous.get("active_seconds") or 0.0)
        self.started = time.monotonic()
        self.fields: dict = {k: previous[k] for k in ("finished_generations", "elo", "anchor", "best", "cap_hours")
                             if k in previous}
        self.progress = None  # a callable giving e.g. "812/2000 games", or None
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._every = every
        self._thread: threading.Thread | None = None

    def hours(self) -> float:
        return self.seconds() / 3600

    def seconds(self) -> float:
        return self.base_seconds + time.monotonic() - self.started

    def update(self, **fields) -> None:
        with self._lock:
            self.fields.update(fields)
            self._write()

    def doing(self, what: str, progress=None) -> None:
        self.progress = progress
        self.update(doing=what)

    def _write(self) -> None:
        data = dict(self.fields)
        if self.progress is not None:
            try:
                data["progress"] = self.progress()
            except Exception:  # progress is informational only
                data["progress"] = None
        else:
            data["progress"] = None
        data["active_seconds"] = round(self.seconds(), 1)
        data["pid"] = os.getpid()
        data["updated"] = now()
        write_json(self.root / STATUS, data)

    def start_heartbeat(self) -> None:
        def beat():
            while not self._stop.wait(self._every):
                with self._lock:
                    try:
                        self._write()
                    except OSError:
                        pass
        self._thread = threading.Thread(target=beat, name="status heartbeat", daemon=True)
        self._thread.start()

    def stop_heartbeat(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)


class LineCounter:
    """Counts the lines of a file that only grows (games.jsonl, match.jsonl), reading only what was added since the
    last call, so a progress check stays cheap however big the file gets."""

    def __init__(self, path: Path):
        self.path = Path(path)
        self.offset = 0
        self.lines = 0

    def __call__(self) -> int:
        try:
            size = self.path.stat().st_size
        except FileNotFoundError:
            return 0
        if size < self.offset:  # rewritten (a resumed session drops a torn line): count again
            self.offset = self.lines = 0
        with open(self.path, "rb") as f:
            f.seek(self.offset)
            chunk = f.read()
        self.lines += chunk.count(b"\n")
        self.offset += len(chunk)
        return self.lines


# --- the one status line ------------------------------------------------------------------------------------------------

def status_line(root: Path, name: str | None = None) -> str:
    """One line: what the Run is doing, how far it has got, the latest Elo, and why it stopped if it did."""
    root = Path(root)
    name = name or root.name
    s = read_status(root)
    running = is_running(root)
    state = s.get("state")
    if not s:
        return f"{name}: not started yet" + (" (starting)" if running else "")
    if state in ("running", "starting") and not running:
        state = "interrupted"
    parts = []
    head = f"{name}: {state}"
    if state == "interrupted":
        head += " (the loop is gone: crash or restart; start continues)"
    elif state == "running" and pause_requested(root):
        head += " (pausing)"
    elif s.get("reason") and state in ("stopped", "paused", "done", "refused"):
        head += f" ({s['reason']})"
    doing = s.get("doing")
    if doing:
        progress = s.get("progress")
        where = "" if state in ("running", "starting") else ("during " if state in ("paused", "interrupted") else "after ")
        if state in ("stopped", "done", "refused") and s.get("finished_generations") is not None:
            doing = None
        if doing:
            parts.append(where + doing + (f", {progress}" if progress else ""))
    done = s.get("finished_generations")
    if done is not None:
        parts.append(f"{done} Generation{'s' if done != 1 else ''} done")
    if s.get("elo") is not None:
        elo = f"Elo {s['elo']:.0f}"
        if s.get("anchor"):
            elo += f" (vs the Anchor {s['anchor']})"
        parts.append(elo)
    if s.get("best") is not None:
        parts.append(f"best Generation {s['best']}")
    hours = float(s.get("active_seconds") or 0) / 3600
    cap = s.get("cap_hours")
    parts.append(f"{hours:.1f}" + (f" of {cap:g}" if cap else "") + " desktop-hours")
    return head + ": " + "; ".join(parts)

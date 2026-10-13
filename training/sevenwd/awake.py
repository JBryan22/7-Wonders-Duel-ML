"""Keep the computer awake while the training loop runs (ticket 27: Windows slept on idle in the middle of pilot 1).

Windows: the loop's main thread asks for ES_CONTINUOUS | ES_SYSTEM_REQUIRED (SetThreadExecutionState), which stops
idle sleep for as long as it is held; the display can still turn off, and the power button / closing a laptop lid
still sleep. Windows drops the request by itself when the process ends, however it ends.
Linux: `systemd-inhibit --what=sleep:idle` around a `cat` that waits on our stdin, so the inhibitor also goes away
when the loop dies. Without systemd-inhibit it does nothing.

Held by loop.py from start to pause / stop / exit. SEVENWD_KEEP_AWAKE=0 switches it off.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys

ES_CONTINUOUS = 0x80000000
ES_SYSTEM_REQUIRED = 0x00000001


def enabled() -> bool:
    return os.environ.get("SEVENWD_KEEP_AWAKE", "1") not in ("0", "false", "no", "off")


def set_thread_execution_state(flags: int) -> int:
    """Windows only: SetThreadExecutionState; returns the previous state (0 on failure)."""
    import ctypes
    f = ctypes.windll.kernel32.SetThreadExecutionState
    f.argtypes = [ctypes.c_uint32]
    f.restype = ctypes.c_uint32
    return int(f(flags))


class KeepAwake:
    """`with KeepAwake():` or acquire() / release(), from the same thread (Windows ties the request to the thread)."""

    def __init__(self):
        self.how: str | None = None  # what holds the machine awake, or None
        self._inhibitor: subprocess.Popen | None = None

    def acquire(self) -> str | None:
        if self.how is not None or not enabled():
            return self.how
        try:
            if sys.platform == "win32":
                if set_thread_execution_state(ES_CONTINUOUS | ES_SYSTEM_REQUIRED):
                    self.how = "Windows idle sleep held off (the display can still turn off)"
            elif shutil.which("systemd-inhibit"):
                self._inhibitor = subprocess.Popen(
                    ["systemd-inhibit", "--what=sleep:idle", "--who=7wd-train", "--mode=block",
                     "--why=training the 7 Wonders Duel network", "cat"],
                    stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                self.how = "sleep inhibited (systemd-inhibit)"
        except (OSError, AttributeError):
            self.how = None
        return self.how

    def release(self) -> None:
        if self.how is None:
            return
        self.how = None
        try:
            if sys.platform == "win32":
                set_thread_execution_state(ES_CONTINUOUS)
            elif self._inhibitor is not None:
                self._inhibitor.stdin.close()  # cat ends, so systemd-inhibit releases
                try:
                    self._inhibitor.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self._inhibitor.kill()
        except OSError:
            pass
        self._inhibitor = None

    def __enter__(self) -> "KeepAwake":
        self.acquire()
        return self

    def __exit__(self, *exc) -> None:
        self.release()

"""Desktop notifications for a Run that stopped itself (spec A7, "Control").

Windows: a toast through Windows PowerShell's built-in WinRT access (no extra packages). Linux: `notify-send` when it
exists. Best effort everywhere: a notification that can't be shown (no desktop session, e.g. over SSH) is only
logged. SEVENWD_NOTIFY=0 switches them off (the tests do).
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
from xml.sax.saxutils import escape

TOAST = r"""
$ErrorActionPreference = 'Stop'
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($env:SEVENWD_TOAST_XML)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
$app = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show($toast)
"""


def enabled() -> bool:
    return os.environ.get("SEVENWD_NOTIFY", "1") not in ("0", "false", "no", "off")


def notify(title: str, message: str) -> bool:
    """Show a desktop notification; returns whether one was shown (as far as we can tell)."""
    if not enabled():
        return False
    try:
        if sys.platform == "win32":
            xml = ("<toast><visual><binding template='ToastGeneric'>"
                   f"<text>{escape(title)}</text><text>{escape(message)}</text>"
                   "</binding></visual></toast>")
            result = subprocess.run(
                ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", TOAST],
                env={**os.environ, "SEVENWD_TOAST_XML": xml}, capture_output=True, timeout=60,
                creationflags=subprocess.CREATE_NO_WINDOW)
            return result.returncode == 0
        tool = shutil.which("notify-send")
        if tool is None:
            return False
        return subprocess.run([tool, title, message], capture_output=True, timeout=30).returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False

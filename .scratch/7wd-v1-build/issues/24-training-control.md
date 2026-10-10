# 24: Starting, pausing and stopping training

**What to build:** 7wd train start/pause/status, Windows shortcuts, checkpointing, resume mid-step, self-stop on plateau / cap / warm-start trigger / fault, notifications.

**Blocked by:** 23

**Status:** ready-for-agent

Spec: `docs/spec-v1.md`.

- [ ] Pause frees the machine within about a minute and loses at most a minute
- [ ] Status shows one line including the stop reason

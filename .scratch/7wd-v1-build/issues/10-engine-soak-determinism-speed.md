# 10: Engine soak, determinism and speed

**What to build:** Random-play invariants over 100k games, cross-OS determinism, and 7wd bench.

**Blocked by:** 09

**Status:** ready-for-agent

Spec: `docs/spec-v1.md`.

- [ ] All invariants hold over 100k games
- [ ] Same seed gives byte-identical logs and state on Linux and Windows (CI)
- [ ] bench at least 10k random games/s on one core (optimise if missed)

# 06: Pantheon soak, scenarios and determinism

**What to build:** `7wd soak/random --expansion pantheon` (with each promo setting), the invariants of P-A10, hand-traced Pantheon scenarios as scripted games, pinned Pantheon determinism hashes in CI, `StatePrinter` and `7wd replay` support, and a Pantheon line in `7wd bench`.

**Blocked by:** 05

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A10), on top of `docs/spec-v1.md`.

- [ ] 100k Pantheon games per promo setting, all invariants hold; victory-type mix recorded in Notes
- [ ] At least 3 hand-traced scenarios with hand-worked scores pass
- [ ] Pantheon hashes pinned and equal on Linux and Windows (CI)
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

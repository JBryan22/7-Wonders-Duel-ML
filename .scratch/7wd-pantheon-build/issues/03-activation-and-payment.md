# 03: Activating the Pantheon and paying for it

**What to build:** The `activate <divinity|gate>` main action from Age II, its costs (per space, doubled for the Gate, −2 with the Sanctuary), the `offer <tokens>` decision (R8), Astarte's coins (R7), and the Divinities with no decision: Ishtar, Astarte, Tanit, Aphrodite, Mars. The Sanctuary Wonder.

**Blocked by:** 02

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A4), on top of `docs/spec-v1.md`.

- [ ] Rule tests: each cost case; affordability with coins + Astarte + Offering tokens; `offer` skipped when holding none; overpaying allowed; treasury spent before Astarte; military loss never touches Astarte coins; Ishtar completes a science pair; Mars moves the pawn and can win
- [ ] Activating an impossible Divinity is allowed and does nothing
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

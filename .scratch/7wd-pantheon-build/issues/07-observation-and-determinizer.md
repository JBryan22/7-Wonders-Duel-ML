# 07: Pantheon Observation and Determinizer

**What to build:** Audience-based hiding of the opponent's placed Divinity, private deck tops and the opponent's Divine Theater restacking order (a private Move, R3) in `Observe`; Advisor-mode redaction (`reveal P2 divinities ? ?`, `P2 theater-order ?`); the Determinizer filling them uniformly, consistent with each face-down card's public Mythology and the deck knowledge.

**Blocked by:** 06

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A5), on top of `docs/spec-v1.md`.

- [ ] No-peeking test with different hidden Divinities and deck tops
- [ ] Invariant: the Observation never holds private Pantheon knowledge before it is public
- [ ] Determinized states are always legal and replayable
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

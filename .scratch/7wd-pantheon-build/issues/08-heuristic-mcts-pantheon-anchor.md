# 08: Heuristic, MCTS and the Pantheon Anchor

**What to build:** Heuristic values for Pantheon (P-A6), `--expansion pantheon` in `7wd match`, the Pantheon Ladder (Random = 0) and the Pantheon Anchor (chance-node MCTS, 1,000 simulations).

**Blocked by:** 07

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A6), on top of `docs/spec-v1.md`.

- [ ] Adaptive balanced Heuristic passes Random in Pantheon games; PIMC passes it; MCTS passes PIMC (Elo and victory-type mix in Notes)
- [ ] The Heuristic activates Divinities in a sensible share of games (reported)
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

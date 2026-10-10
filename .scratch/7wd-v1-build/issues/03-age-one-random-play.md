# 03: First playable slice: Age I with random players

**What to build:** A random game of Age I plays end to end: Game State with cheap copy, the take-card Pending Decision (build / discard), cheapest automatic payment with trading, Reveals for flipped cards from a seeded Chance Source, the Random Agent, Move Log writing and replay, and 7wd replay printing the state.

**Blocked by:** 02

**Status:** ready-for-agent

Spec: `docs/spec-v1.md`.

- [ ] Random Age I games always end with all 20 cards taken
- [ ] Replaying a written Move Log reproduces an identical Game State
- [ ] Payment rule tests: trading prices count only the opponent's brown/grey; choice resources go on the most expensive missing resource
- [ ] Coins never go negative over 10k random Age I games

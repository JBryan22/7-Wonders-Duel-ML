# Assemble the v1 spec and phased build plan

Type: task (AFK)
Status: resolved
Assignee: Jesse Bryan
Blocked by: 06, 07, 08, 09, 10, 11, 12, 13, 16, 18, 19, 20
Spec: `docs/spec-v1.md` (on main)

## Question

Combine the resolved decisions into the destination: a v1 spec (`docs/spec-v1.md`) plus a phased build plan in which each phase ends in something runnable and a validation experiment that proves it works (see "Learning track format"), ready to hand to `/to-tickets` or to implementation.

## Answer

Done on 2026-10-09: [`docs/spec-v1.md`](../../../docs/spec-v1.md). Nothing new was decided. The spec gathers every resolved ticket and ADR into one hand-off, citing each ticket by name.

**Part A, the v1 spec:** scope and the v1 done condition; stack, machines and repo; rules and card data; Engine (Ruleset, Reveals with audiences, Game State, Pending Decisions and Moves including the promos, Observation, Move Log, Determinizer); Agents and Matches; Encoding v1 (folding the promo changes into the input and policy tables: about 2,300 inputs and 1,510 policy outputs); training pipeline, budget and stopping; Advisor Window; Adapters; extension points; testing; a list of implementation checks deliberately left to the build.

**Part B, the build plan:** 11 phases, each ending in something runnable and a validation experiment:
P0 Foundations → P1 Base Engine → P2 cross-check and P3 promo Wonders → P4 Match harness and Heuristic → P5 PIMC and chance-node MCTS (the Anchor) → P6 Encoding, network and neural search → P7 training loop and Run control → P8 calibration, pilots and main Run. P9 Advisor Window and P10 TTS Adapter branch off after P5 and are built while the main Run trains.

**v1 is done** when the "best" network passes the Anchor (P8) and the owner plays one full TTS game with advice from it, fed automatically by the TTS Adapter (P10).

**Small consolidations** (not new decisions):
- The Statue of Liberty is two Moves, `liberty-keep` then `liberty-give`, as in "Promo Wonders in the Engine, training and Encoding". This replaces the research's single `liberty <keep> <give>`.
- Gumbel's own parameters start from the published defaults. They are listed with the other build-time checks.

**Next:** hand the build plan to `/to-tickets`, or start building at P0.

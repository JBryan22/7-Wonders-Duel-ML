# Agent Ladder and target algorithm

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 04, 05

## Question

Which Agents make up the Agent Ladder (and in what order we build them), which algorithm is the final neural Agent, how is hidden information handled during search, and how do we measure strength between rungs (Elo / win-rate matches, number of games for significance)? Each rung should come with the "why this beats the previous rung" lesson.

## Answer

Resolved by grilling on 2026-10-09. New glossary term: Match.

**Agent Ladder, built in this order.** Each rung must beat the one before it.

| Rung | What it is | Lesson ("why it beats the previous rung") |
|---|---|---|
| Random | Any legal Move | The zero point on the strength scale, and the first stress test for the Engine |
| Heuristic | A hand-written scoring function with configurable weights, shipped as presets: science, military, civilian and **adaptive balanced** | Domain knowledge beats chance |
| PIMC | 50 guesses of the hidden cards, 1 random playout each, pick the Move that wins most | Simulation can replace hand-written knowledge |
| Chance-node MCTS | A tree search that samples each hidden card when it is revealed, 1,000 simulations | Smart exploration beats uniform sampling |
| Neural (Gumbel AlphaZero) | MCTS guided by a policy/value network trained by Self-Play | Learned intuition replaces random playouts |

- **Adaptive balanced Heuristic.** It recomputes its weights each turn from board signals: opponent science symbols, distance of the Conflict Pawn to a capital, point lead. So it starts out neutral and pivots between victory types, the way a strong player does. The style presets keep fixed weights and serve as sparring partners and seed material.
- **The neural Agent is the one that truly adapts.** It doesn't use a style. Its value network judges the whole board, so pivoting falls out of learning.
- PPO is dropped. ReBeL and Student of Games are out (see "Learning algorithms for a hidden-information, chancy two-player game").

**Gumbel is the target from day one.** There is one neural search with a root-mode switch: classic AlphaZero or Gumbel. They share the network, Self-Play and training. Classic mode is scaffolding: we first run a short smoke run in it and check it against reference implementations, so a bug in the search can be told apart from "the network isn't learning". All real training uses Gumbel. The teaching moment is one experiment: classic vs Gumbel at an equal simulation count. Classic AlphaZero is not a rung.

**Hidden information in search: Observation only, everywhere.** Every search starts from the Observation, never the true Game State, even in Self-Play where the true state is available. Every Reveal in the simulated future is drawn uniformly from the pool this player hasn't seen, and that includes the opponent's Great Library draw. This puts ADR 0002 into practice and rules out the "search peeks at face-down cards" pitfall from prior art.

**Seeding: start with pure Self-Play, keep a warm start ready as the fix.** If, after a few thousand games, the victory-type mix in Match reports never shows science wins, switch to a warm start: first train the network to imitate games between the Heuristic presets, then continue with Self-Play. A third option is to mix in 10–20% of games against the presets and fade that share to 0. The lesson is watching it get stuck, then fixing it.

**Measuring strength.**
- Agents are compared in a **Match**: 400 games in 200 seed pairs, with seats swapped inside each pair so both sides get the same luck. No SPRT in v1.
- Results are reported as win rate ± 95% CI, plus **Elo** on one shared scale with Random fixed at 0. A 200-point gap ≈ 76% expected win rate.
- Every Match records the victory-type breakdown (civilian / military / science).
- A rung **passes** when the lower 95% bound of its win rate against the previous rung is above 50%.
- Search Agents are compared at a fixed simulation count, not wall-clock time. Wall-clock time is logged separately.

**Simulation budgets** are starting defaults, to be revised once Engine and network speed are measured:
- PIMC: 50 × 1
- MCTS: 1,000
- Neural: 64 Gumbel simulations in training, 800 in Matches and the Advisor

**The Advisor's thinking budget** is a fixed simulation count by default, which is reproducible and comparable with Match results, plus a "think longer" option. The exact numbers are for the compute-budget work.

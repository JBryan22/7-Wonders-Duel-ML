# Training compute budget and v1 stopping rule

Type: grilling
Status: open
Blocked by:

## Question

How much training does v1 get, and how do we decide the network is "good enough" to call v1 done? We can't measure Engine or network speed before the code exists, so this ticket decides the **rules** for setting the numbers, not the numbers themselves:
- **Stopping rule:** e.g. a target Elo margin over the strongest non-neural rung (chance-node MCTS at a fixed budget), an Elo plateau over N Generations, a GPU-hours cap, or a combination.
- **Budget:** how many GPU-hours or days on the Windows desktop the owner will commit to a v1 run, and to the experiments already planned (Small vs Medium network, with vs without derived features, promo rate).
- **Tuning procedure:** once `7wd bench` and network throughput are measured, how the starting defaults (2,000 games per Generation, a ~1M-position window, 64 simulations) are revisited.
- **Advisor search budget:** how many simulations, or how many seconds, the Advisor uses per request on the desktop and on the laptop.
- **Heuristic warm start:** the trigger for switching it on, e.g. no science wins in Self-Play after N Generations.

## Context

- The algorithm and the loop are fixed: see "Agent Ladder and target algorithm" and "Training pipeline and two-machine workflow".
- Self-Play always includes both promo Wonders, and the headline Elo is measured on base-only Matches ("Promo Wonders in the Engine, training and Encoding").
- Speed target: `7wd bench` at least 10k random games/s ("Solution structure and Engine testing strategy").

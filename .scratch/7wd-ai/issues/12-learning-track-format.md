# Learning track format

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by:

## Question

The owner wants to learn ML through this project. In what form does the learning happen — short explainer docs per Agent Ladder rung, runnable experiments (e.g. "train with half the data, watch Elo"), exercises where the owner writes a piece themselves, Jupyter notebooks for the Python side — and which parts does the owner want to write versus read?

## Answer

**There is no learning track.** The owner changed course: Claude builds all of the code, and the owner writes none. Later, on request, Claude walks the owner through the finished project and explains it as needed. The aim is understanding (why each Agent Ladder rung works, how to read the training charts), not being able to build it unaided.

- **Phases end in a validation check, not a learning milestone.** Each build phase ends in something runnable plus an experiment that proves it works. Examples: the Heuristic beats Random by a clear Elo margin; chance-node MCTS beats PIMC; the network's Elo rises across Generations. These are engineering acceptance criteria.
- **No explainer docs or exercises.** Nothing extra is written for teaching. ADRs, ticket answers and the spec already record the *why* in plain language, and the later walkthrough is built from the real code so it can't go stale.
- **Earlier decisions stand on engineering grounds alone:**
  - The Agent Ladder stays. Each rung is an Elo baseline and a debugging stepping stone.
  - Classic AlphaZero mode stays, as debug scaffolding only (it tells "search bug" apart from "network not learning"). The classic-vs-Gumbel teaching experiment is no longer required.
- **Working style:** Claude still explains ML concepts in plain terms when putting a decision to the owner, because the owner still makes the decisions. CLAUDE.md and the map's Notes are updated to match.

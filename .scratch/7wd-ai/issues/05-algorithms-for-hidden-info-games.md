# Learning algorithms for a hidden-information, chancy two-player game

Type: research
Status: open
Blocked by:
Research: branch `research/algorithms` → `docs/research/algorithms.md`

## Question

Which game-playing algorithms suit 7 Wonders Duel — two-player, zero-sum, with hidden information (face-down cards, removed cards, unseen progress tokens) and chance (card reveals) — and what are their trade-offs in strength, implementation complexity, compute needs, and learnability for an ML newcomer? Cover at least: heuristic Agents, MCTS with determinization (Perfect-Information MC), Information-Set MCTS, AlphaZero with determinization, Stochastic MuZero / Gumbel AlphaZero, ReBeL / Student of Games, and PPO-style policy-gradient self-play. Include rough compute expectations for a game of this size on one consumer NVIDIA GPU and practical notes on running ONNX models from C# for batched search inference.

Deliverable: findings doc in `docs/research/`, written so a newcomer can follow it (plain-language explanations first, details after).

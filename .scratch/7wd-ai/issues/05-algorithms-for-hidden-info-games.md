# Learning algorithms for a hidden-information, chancy two-player game

Type: research
Status: resolved
Blocked by:
Research: `docs/research/algorithms.md` (on main)

## Question

Which game-playing algorithms suit 7 Wonders Duel — two-player, zero-sum, with hidden information (face-down cards, removed cards, unseen progress tokens) and chance (card reveals) — and what are their trade-offs in strength, implementation complexity, compute needs, and learnability for an ML newcomer? Cover at least: heuristic Agents, MCTS with determinization (Perfect-Information MC), Information-Set MCTS, AlphaZero with determinization, Stochastic MuZero / Gumbel AlphaZero, ReBeL / Student of Games, and PPO-style policy-gradient self-play. Include rough compute expectations for a game of this size on one consumer NVIDIA GPU and practical notes on running ONNX models from C# for batched search inference.

Deliverable: findings doc in `docs/research/`, written so a newcomer can follow it (plain-language explanations first, details after).

## Answer

Full findings: `docs/research/algorithms.md`.

- In the base game neither player knows the face-down cards, the 3 removed cards per age, or the unused progress tokens. The only private information is the Great Library peek, and those tokens are discarded. So 7WD is in practice a **perfect-information game with chance**, like backgammon.
- Guessing hidden cards (determinization) works well here, as long as each face-down card is sampled at the moment it is revealed (a chance node), using only what the player can see. That avoids strategy fusion, and non-locality hardly applies.
- Suggested ladder: Random → Heuristic → PIMC → MCTS with chance nodes → AlphaZero with a network that sees only the Observation → Gumbel AlphaZero, so far fewer simulations per move are needed.
- ReBeL and Student of Games are overkill for v1. Stochastic MuZero contributes only the idea of explicit chance nodes. PPO is an optional comparison point.
- Rough compute estimates (unmeasured): about 70–80 decisions per game, 5–20 legal moves per decision, about 10⁵ network evaluations per second and 5–40 self-play games per second on one consumer GPU.
- C#: the `Microsoft.ML.OnnxRuntime.Gpu` package runs on Windows and Linux (CUDA 13 + cuDNN 9 from v1.27). Many parallel games send network calls to one batching inference thread.
- Open: Great Library details need confirming by the rules reference. Expansion private information (Agora Conspiracies) was not studied.

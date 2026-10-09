# Prior art: existing 7 Wonders Duel engines and AIs

Type: research
Status: resolved
Blocked by:
Research: branch `research/prior-art` → `docs/research/prior-art.md`

## Question

What open-source 7 Wonders Duel rules engines, AIs, datasets or write-ups already exist (GitHub, papers, blog posts, BoardGameGeek), what approaches did they use (heuristics, MCTS, ISMCTS, AlphaZero-style RL), how strong did they get, and what can we reuse or learn from (card data files, test cases, encodings, pitfalls)? Note licenses.

Deliverable: findings doc in `docs/research/`.

## Answer

Full findings: `docs/research/prior-art.md` on branch `research/prior-art`.

- **The only strong AI is closed source.** ZeusAI (Paolini et al., CoG 2024, arXiv 2406.00741) uses AlphaZero-style MCTS with a ~92M-parameter Transformer, trained on ~420k self-play games. It won 26 of 38 games against top BGA players. The code is withheld over cheating concerns. Hardware and encoding are unpublished.
- **No open project has a neural agent with measured strength.** Open AIs:
  - indjev99: C++, MIT, MCTS with heuristic rollouts.
  - marcotama and karthik24iyer: Kotlin, no licence, parallel MCTS.
  - JrCrespi (Python) and MC-freshman (TypeScript): no licence, alpha-beta search.
  - kilianmandon: Python, MIT, AlphaZero and PPO work in progress, no results.
- **Strong agents treat hidden cards as chance:** they sample each card when it is revealed. This matches "Learning algorithms for a hidden-information, chancy two-player game", so ISMCTS isn't needed.
- **Engine speed:** on this laptop the C++ engine ran about 66k random games per second on one core, and the pure-Python engine about 67 per second. That's roughly 1000× and strongly supports ADR 0001.
- **Pitfalls to avoid:**
  - kilianmandon's search peeks at face-down cards and skips the wonder draft.
  - JrCrespi gives guild cards the wrong back.
  - Science wins are rare enough that ZeusAI needed about 100k games to discover them. It seeded training with games from green- and red-focused rule-based bots. Input for "Agent Ladder and target algorithm" and the training budget.
- **Reusable:**
  - kilianmandon's card and layout JSON (MIT) and AlexisDeseure's JSON (BSD-3), with attribution.
  - The BGA PHP code (koenheltzel), the most authoritative source and the one that includes Pantheon and Agora. Its licence is BGA-only, so use it as a reference only.
  - Test ideas from karthik's rulebook tests and MC-freshman's validators.
- **Expansions:** Pantheon and Agora appear only in BGA and MC-freshman (Agora data partly approximate). No AI work covers them.

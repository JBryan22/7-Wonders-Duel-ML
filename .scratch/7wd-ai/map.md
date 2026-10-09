# Map: 7 Wonders Duel AI

Label: wayfinder:map

## Destination

A build-ready **v1 spec + phased build plan** for a 7 Wonders Duel (base game) AI: a C# Engine, an Agent Ladder culminating in a self-play-trained neural Agent, and Advisor-mode Adapters for manual entry and Tabletop Simulator — with named extension points for Pantheon/Agora and for an AI-controlled seat.

## Notes

- Domain: board-game AI / reinforcement learning. Vocabulary in `GLOSSARY.md`; decisions in `docs/adr/`.
- Every grilling session: call the Skill tool for "grilling" and "domain-modeling".
- The owner writes no code: Claude builds everything. The owner is new to ML and still makes the decisions, so explain ML concepts in plain terms when putting questions. Understanding comes later, from a walkthrough on request, not from a learning track (see "Learning track format").
- Stack is decided: C# Engine/search/Self-Play/Adapters, Python/PyTorch training only, ONNX hand-off ([ADR 0001](../../docs/adr/0001-csharp-engine-python-training.md)).
- Machines: Linux laptop = development (no NVIDIA GPU). Windows desktop = NVIDIA training + Tabletop Simulator. All software must run on both. Move work to Windows only when TTS work needs it.
- Rules: official current English rulebook, no house rules.
- TTS mod: "7 Wonders Duel [ULTIMATE ed.]" — https://steamcommunity.com/sharedfiles/filedetails/?id=3368311210
- Real-play honesty: the Agent decides only from an Observation, never hidden Game State. Advisor use is for practice/friendly games, not online competitive play.

## Decisions so far

<!-- one line per closed ticket: [title](issues/NN-slug.md): gist -->

- [Base game rules reference](issues/01-base-game-rules-reference.md): rules from the 2015 rulebook. Card data was cross-checked across two engines (all 73 cards and 12 Wonders agree). Use stable IDs, use the newer start-player-choice rule behind a flag, and model the visible Guild card backs.
- [Prior art: existing 7 Wonders Duel engines and AIs](issues/04-prior-art-7wd-ai.md): the only strong AI (ZeusAI, AlphaZero-style) is closed source, and no open neural agent exists. Strong agents treat hidden cards as chance. The C++ engine is ~1000× faster than Python. Science wins need seeded training. Card JSON is reusable under MIT.
- [Reading the game from Tabletop Simulator](issues/02-tts-mod-and-api.md): the mod only does setup and scoring. Read the board by name and tag through the External Editor API, and add our own event-listener object. The snapshot must filter out hidden cards.
- [Learning algorithms for a hidden-information, chancy two-player game](issues/05-algorithms-for-hidden-info-games.md): base 7WD is in practice a perfect-information game with chance. Ladder: Random → Heuristic → PIMC → chance-node MCTS → AlphaZero (sees only the Observation) → Gumbel AlphaZero.
- [Game State, Observation and Move model](issues/06-game-state-and-move-model.md): hidden cards get their identity only when a Chance Source reveals them (ADR 0002). Multi-step choices are Pending Decisions resolved one Move at a time, and payment is always computed automatically. The Observation is the Game State minus the opponent's Great Library draw. The Move Log is plain text with slug IDs, and the state is mutable with cheap copies.
- [Agent Ladder and target algorithm](issues/07-agent-ladder-and-algorithm.md): the ladder is Random → Heuristic (presets, including an adaptive balanced one) → PIMC → chance-node MCTS → Gumbel AlphaZero, with classic AlphaZero kept only as a debug and teaching mode. Search sees only the Observation. Training starts as pure Self-Play, with a heuristic warm start ready if science wins never appear. Strength is measured in seat-swapped 400-game Matches with Elo (Random = 0), using fixed simulation budgets.
- [Neural network input and output encoding](issues/08-network-encoding.md): a flat ~2,200-number Observation from the mover's view: slot-centric layout, card-centric cities and discard pile, plus derived features shared with the Heuristic. A ~1,140-way masked policy indexed by card and Wonder. A value head plus an auxiliary 7-class outcome head. A residual MLP starting at 256×4. C# is the only encoder; a versioned manifest is the contract with Python (ADR 0003).
- [Manual-entry Adapter: how it looks and behaves](issues/09-manual-entry-adapter.md): the Board variant won and becomes the Advisor Window. It is a local web page with a clickable pyramid, a picker for naming flipped cards, advice on request (% badges plus a top-3 panel) and undo or rewind by Move Log line. Tabletop Simulator feeds it automatically, and the owner corrects it by clicking. Screen capture was rejected in favour of the TTS API.
- [Training pipeline and two-machine workflow](issues/11-training-pipeline-and-workflow.md): synchronous Generations (C# Self-Play → Python training → C# Matches), driven by a resumable Python `loop.py`. Game Records are kept forever; encoded `.npy` shards are a cache. No gating, but a promoted "best" network for the Advisor. Learning is watched in TensorBoard plus `generations.csv`, and each run has a frozen config. Windows owns runs; the laptop runs a tiny CPU config, with SSH over Tailscale.
- [Learning track format](issues/12-learning-track-format.md): no learning track. Claude writes all the code, and the owner gets a walkthrough on request later. Build phases end in validation experiments, not learning milestones. The Agent Ladder and classic AlphaZero debug mode stay for engineering reasons.
- [Solution structure and Engine testing strategy](issues/13-solution-structure-and-testing.md): the repo has `engine/` (.NET 10: Engine, Agents, Cli, an Advisor serving plain HTML/JS, and tests), `training/` (uv, CPU or CUDA PyTorch chosen by platform), `data/` (card JSON plus the committed Encoding manifest) and `docs/`. The Engine is proven by rule tests, independently scored scripted games, random-play invariants, data tests and an opt-in cross-check against indjev99's C++ engine. Seeds give identical games on both operating systems. CI runs on Linux and Windows, plus a tiny-loop smoke test. `7wd bench` targets 10k random games/s.

## Not yet specified

- **Expansion extension points**: where Pantheon (gods, divinities, mythology/offering tokens) and Agora (senators, conspiracies, decrees) hook into the Game State, Move set and network encoding. Decided so far: expansions arrive as a new Encoding version that adds input and policy blocks, with no reserved slots (see "Neural network input and output encoding"). Still open: the Game State and Move hooks; may need its own rules research.
- **Advice presentation**: how a recommendation is shown and how much "why" comes with it (top-N moves with win %, principal line, explanations). The Gumbel AlphaZero Agent can expose per-Move visit shares, a value (win %), the expected victory type (from the outcome-class head) and a principal line; shown in the Advisor Window as % badges on cards plus a top-3 panel with win % and a short reason (see "Manual-entry Adapter: how it looks and behaves"). Still open: what the short reason is built from, and whether the principal line is shown.
- **Handling setup and chance inside Adapters**: each Adapter is a Chance Source, and undo means truncating and replaying the Move Log (see the Game State ticket). Manual entry is settled: a flipped card blocks input until it is named from a picker. Still open: how TTS detects Reveals and recovers from desyncs, which "Tabletop Simulator Adapter architecture" should answer.
- **Compute budget and stopping rule for training**: how many Self-Play games / GPU-hours, and how we decide the network is "good enough" for v1. The algorithm and the loop are now fixed (Gumbel, 64 simulations, synchronous Generations with starting defaults of 2,000 games and a ~1M-position window; see "Training pipeline and two-machine workflow"), so this means tuning those defaults against measured Engine and network speed (`7wd bench`, target at least 10k random games/s; see "Solution structure and Engine testing strategy"). Also open: the exact Advisor simulation counts, and when the heuristic warm start kicks in.
- **AI-controlled seat seam**: what the Adapter contract must include now so a later "AI moves the pieces in TTS" mode is additive.

## Out of scope

- Building Pantheon/Agora support (v1 only reserves extension points for them).
- Adapters for Board Game Arena or the official digital app (screen-reading; using a bot in live play breaks their terms).
- Implementing AI-controlled-seat mode in TTS (only its seam is in scope).

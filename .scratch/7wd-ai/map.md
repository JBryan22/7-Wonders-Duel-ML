# Map: 7 Wonders Duel AI

Label: wayfinder:map

## Destination

A build-ready **v1 spec + phased build plan** for a 7 Wonders Duel (base game) AI: a C# Engine, an Agent Ladder culminating in a self-play-trained neural Agent, and Advisor-mode Adapters for manual entry and Tabletop Simulator — with named extension points for Pantheon/Agora and for an AI-controlled seat.

## Notes

- Domain: board-game AI / reinforcement learning. Vocabulary in `GLOSSARY.md`; decisions in `docs/adr/`.
- Every grilling session: call the Skill tool for "grilling" and "domain-modeling".
- The owner is new to ML and wants this as a learning journey: decisions should favor a staged Agent Ladder (random → heuristic → search → neural Self-Play), each stage with a short "why it works" explainer. Explain ML concepts in plain terms when putting questions.
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

## Not yet specified

- **Expansion extension points**: where Pantheon (gods, divinities, mythology/offering tokens) and Agora (senators, conspiracies, decrees) hook into the Game State, Move set and network encoding. Sharpens once the Game State model and encoding exist; may need its own rules research then.
- **Advice presentation**: how a recommendation is shown and how much "why" comes with it (top-N moves with win %, principal line, explanations). Depends on what the chosen Agent can expose and on the Adapter UX.
- **Handling setup and chance inside Adapters**: how an Adapter learns age-layout reveals, removed cards, and drawn progress tokens it can't see yet — and recovers from mis-entries or desyncs.
- **Compute budget and stopping rule for training**: how many Self-Play games / GPU-hours, and how we decide the network is "good enough" for v1. Depends on algorithm choice and measured Engine speed.
- **AI-controlled seat seam**: what the Adapter contract must include now so a later "AI moves the pieces in TTS" mode is additive.

## Out of scope

- Building Pantheon/Agora support (v1 only reserves extension points for them).
- Adapters for Board Game Arena or the official digital app (screen-reading; using a bot in live play breaks their terms).
- Implementing AI-controlled-seat mode in TTS (only its seam is in scope).

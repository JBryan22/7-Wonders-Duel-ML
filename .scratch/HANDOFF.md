# Handoff (2026-10-15)

Read this first when picking the project up in a new session or on the other machine.

## Where things stand

- **v1 build:** tickets 01–34 and 36–37 in `.scratch/7wd-v1-build/issues/` are done. Ticket 27 (pilots) is done: pilots 3 and 4 were skipped by the owner. Open: **28** (main run, in progress on the desktop) and **35** (the owner's acceptance game in TTS; the live TTS checks in `docs/tts-live-checks.md` haven't been done yet).
- **Main run** (Windows desktop, `C:\7wd-runs\main`, config `training/configs/main.yaml`): warm-started from pilot 1, Small 256x4, Gumbel self-play, classic search in Matches, cap 150 desktop-hours, plateau rule 20 Generations. At Generation 12 / 17 h: Anchor 99%, reference (`mcts:sims=20000`) 96%, Anchor Elo ~2271. Gains per Generation are shrinking (~54% vs previous). The owner controls it with the desktop shortcuts Start training / Pause training / Training status.
- **Owner vs AI** (Play against the AI in the Advisor Window): beat pilot 1 Gen 4; lost twice to main Gen 4 (one science win).

## Next: Pantheon (owner's decision, 2026-10-15)

Build Pantheon on the **Linux laptop** while the main run keeps training on the desktop; train Pantheon on the desktop later.

1. Turn the research in `.scratch/7wd-ai/issues/17-pantheon-agora-rules-for-extension-points.md` and `18-expansion-extension-points.md` (and the spec's extension points in `docs/spec-v1.md`) into a ticket set in `.scratch/7wd-pantheon-build/issues/`, same format as the v1 tickets: rules in the Engine (Pantheon board and gods, Mythology and Offering tokens, Divinity powers, Grand Temples), log grammar, soak/determinism, Observation and Determinizer, Heuristic/MCTS support, Encoding v2 + manifest, network surgery (copy the trunk from the base network, fresh new input/output parts), Advisor and play-vs-AI support, TTS names/places for the Pantheon pieces, then Pantheon training (desktop, ready-for-human).
2. **Base game must not change:** determinism hashes and the C++ `FrozenGames/` must stay identical; Encoding v1 and existing networks must keep working. Pantheon is enabled by the rules header (`expansion=pantheon`, today refused as "not supported").
3. The desktop must **not** pull Pantheon code while the main run is in progress unless the base game's tests and hashes are unchanged (the loop rebuilds `7wd` at every start).

## How the owner likes the work run

- Work tickets top to bottom without check-ins; commit and **push after each ticket**; stop only at ready-for-human tickets or real blockers.
- The main session orchestrates: one fresh sub-agent per ticket, sequentially; each reads the spec and ticket, builds, tests, closes the ticket with Notes, commits, pushes and returns a short report (≤150 words). Pass each builder the previous builders' handoff notes.
- Runs never go inside the repo (desktop: `C:\7wd-runs`). The owner writes no code and is new to ML: explain ML choices in plain terms.

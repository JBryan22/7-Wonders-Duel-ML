# 7 Wonders Duel Player

An ML agent that learns 7 Wonders Duel by self-play and advises the owner while playing in Tabletop Simulator (mod: "7 Wonders Duel [ULTIMATE ed.]", Workshop id 3368311210). Base game first; Pantheon/Agora later.

- Planning lives in `.scratch/7wd-ai/map.md` (a `/wayfinder` map; tickets in `.scratch/7wd-ai/issues/`). Read the map before starting work.
- Vocabulary: `GLOSSARY.md`. Decisions: `docs/adr/`.
- The owner writes no code; Claude builds everything. The owner is new to ML but makes the decisions: explain the *why* behind ML choices in plain terms when asking. Walk them through the project on request.
- Machines: Linux laptop (development, no NVIDIA GPU); Windows desktop with an NVIDIA GPU (training + Tabletop Simulator). Everything must run on both.

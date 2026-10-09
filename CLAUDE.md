# 7 Wonders Duel Player

An ML agent that learns 7 Wonders Duel by self-play and advises the owner while playing in Tabletop Simulator (mod: "7 Wonders Duel [ULTIMATE ed.]", Workshop id 3368311210). Base game first; Pantheon/Agora later.

- Planning lives in `.scratch/7wd-ai/map.md` (a `/wayfinder` map; tickets in `.scratch/7wd-ai/issues/`). Read the map before starting work.
- Vocabulary: `GLOSSARY.md`. Decisions: `docs/adr/`.
- The owner is new to ML and wants to learn: explain the *why* behind ML choices, and favor a staged approach they can follow.
- Machines: Linux laptop (development, no NVIDIA GPU); Windows desktop with an NVIDIA GPU (training + Tabletop Simulator). Everything must run on both.

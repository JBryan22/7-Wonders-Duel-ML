# 09: Encoding v2 and its manifest

**What to build:** Encoding v2 (P-A7): v1's blocks unchanged, Pantheon blocks appended; `7wd manifest --version 2`; `data/encoding/v2.json` with its own test; C# encodes by the network's declared version and refuses a Pantheon game with a v1 network.

**Blocked by:** 07

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A7), on top of `docs/spec-v1.md`.

- [ ] The first v1-size entries of a v2 input equal the v1 input for every base-game position (soaked)
- [ ] Every legal Pantheon Move maps to a unique policy index and back; illegal ones masked
- [ ] Input unchanged when only hidden Pantheon cards change
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

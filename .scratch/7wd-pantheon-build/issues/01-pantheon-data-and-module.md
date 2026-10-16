# 01: Pantheon data and the expansion module

**What to build:** The `data/pantheon/` files (15 Divinities + Gate, 5 Grand Temples, 10 Mythology and 3 Offering tokens, 2 Wonders, 3 Progress tokens, the Pantheon board's 6 spaces with both players' costs, the R1 token slots), their loader, and the empty expansion-module seam: `expansion=pantheon` is accepted by the Ruleset (Agora still refused), the module hooks (setup, legal Moves, apply, end of turn, scoring) exist, and its optional state block is empty in base games. Check which board end is cheap for which player against the rulebook board picture and the Engine's capital orientation.

**Blocked by:** —

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A2, P-A3, P-A4 "Engine structure"), on top of `docs/spec-v1.md`.

- [ ] Data tests: counts, every Divinity in exactly one Mythology, Temple costs and Mythologies, board costs 3–8 summing to 11 per space, token slots are face-down slots
- [ ] `rules base expansion=pantheon` parses and creates a game; `expansion=agora` is still refused
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes
